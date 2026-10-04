using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Valhalla
{
    internal sealed class RelayTelemetryEvent
    {
        public string Fingerprint { get; set; } = string.Empty;
        public Dictionary<string, string> Telemetry { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long TimestampMs { get; set; }
        public long SequenceNumber { get; set; }
    }

    internal sealed class RelayCommandResult
    {
        public string RequestId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public int Queued { get; set; }
        public int Dropped { get; set; }
        public string Detail { get; set; } = string.Empty;

        public bool Accepted => string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                                 (string.Equals(Status, "partial", StringComparison.OrdinalIgnoreCase) && Queued > 0);
    }

    internal sealed class RelayGatewayClient : IDisposable
    {
        private const int MaxFrameBytes = (3 * 1024 * 1024) + (64 * 1024);
        private const int ChallengeBytes = 32;
        private const int ConnectTimeoutMilliseconds = 10000;
        private const int DefaultCommandTimeoutMilliseconds = 30000;
        private const string AuthDomain = "VALHALLA-PANEL-AUTH-V1\0";

        private readonly object sendLock = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayCommandResult>> pending =
            new ConcurrentDictionary<string, TaskCompletionSource<RelayCommandResult>>(StringComparer.Ordinal);

        private TcpClient client;
        private SslStream ssl;
        private Thread readerThread;
        private Timer keepAliveTimer;
        private volatile bool stopping;
        private X509Certificate2 caCertificate;
        private string panelId = string.Empty;

        public event Action<RelayTelemetryEvent> TelemetryReceived;
        public event Action<RelayCommandResult> CommandResultReceived;
        public event Action<string> Disconnected;
        public event Action<string> OnionAddressReceived;

        public bool IsConnected => !stopping && client != null && client.Connected && ssl != null;

        public void Connect(string host, int port, string panelIdValue, string secret, string caPath)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Relay server address is required.", nameof(host));
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Relay server port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(panelIdValue)) throw new ArgumentException("Panel ID is required.", nameof(panelIdValue));
            ValidateSecret(secret);
            if (string.IsNullOrWhiteSpace(caPath) || !File.Exists(caPath)) throw new FileNotFoundException("CA certificate file was not found.", caPath);

            Disconnect("replaced by a new connection");

            caCertificate = RelaySettingsStore.LoadCertificate(caPath);
            RelaySettingsStore.ValidateCaCertificate(caCertificate);
            panelId = panelIdValue;
            stopping = false;
            bool requireCertificateNameMatch = !IPAddress.TryParse(host, out _);

            TcpClient tcp = new TcpClient();
            try
            {
                IAsyncResult connect = tcp.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds))
                    throw new TimeoutException("Relay TCP connection timed out.");
                tcp.EndConnect(connect);
                tcp.NoDelay = true;
                ssl = new SslStream(tcp.GetStream(), false,
                    (sender, certificate, chain, errors) => ValidateServerCertificate(certificate, errors, requireCertificateNameMatch));
                ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);

                client = tcp;
                AuthenticatePanel(secret);
                readerThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "Valhalla-Relay-Reader"
                };
                readerThread.Start();
                keepAliveTimer = new Timer(SendKeepAlive, null, 30000, 30000);
            }
            catch
            {
                try { ssl?.Dispose(); } catch { }
                try { tcp.Close(); } catch { }
                ssl = null;
                client = null;
                caCertificate?.Dispose();
                caCertificate = null;
                throw;
            }
        }

        public async Task<RelayCommandResult> SendCommandAsync(string target, string command, CancellationToken cancellationToken)
        {
            if (!IsConnected) throw new InvalidOperationException("Relay connection is not active.");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Command target is required.", nameof(target));
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<RelayCommandResult> completion =
                new TaskCompletionSource<RelayCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(requestId, completion))
                throw new InvalidOperationException("Unable to register relay command request.");

            try
            {
                Dictionary<string, object> message = new Dictionary<string, object>
                {
                    ["protocol_version"] = 1,
                    ["message_type"] = "agent_command",
                    ["request_id"] = requestId,
                    ["target"] = target,
                    ["command"] = command
                };
                SendJson(message);

                Task timeoutTask = Task.Delay(DefaultCommandTimeoutMilliseconds, cancellationToken);
                Task finished = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
                if (finished == completion.Task)
                    return await completion.Task.ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw new TimeoutException("Timed out waiting for relay command routing response.");
            }
            finally
            {
                pending.TryRemove(requestId, out _);
            }
        }

        public void Disconnect(string reason)
        {
            if (stopping && client == null && ssl == null) return;
            stopping = true;
            try { keepAliveTimer?.Dispose(); } catch { }
            keepAliveTimer = null;
            try
            {
                lock (sendLock)
                {
                    if (ssl != null)
                    {
                        try { SendJsonLocked(new Dictionary<string, object> { ["protocol_version"] = 1, ["message_type"] = "panel_close" }); } catch { }
                        try { ssl.Close(); } catch { }
                        try { ssl.Dispose(); } catch { }
                    }
                }
            }
            finally
            {
                try { client?.Close(); } catch { }
                client = null;
                ssl = null;
                caCertificate?.Dispose();
                caCertificate = null;
                FailPending(reason ?? "relay disconnected");
            }
        }

        public void Dispose()
        {
            Disconnect("relay client disposed");
        }

        private void AuthenticatePanel(string secret)
        {
            byte[] challenge;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                SendJson(new Dictionary<string, object>
                {
                    ["protocol_version"] = 1,
                    ["message_type"] = "panel_hello",
                    ["panel_id"] = panelId
                });

                Dictionary<string, object> response = ReadJsonBlocking(10000);
                if (!string.Equals(GetString(response, "message_type"), "panel_challenge", StringComparison.Ordinal) ||
                    GetInt(response, "protocol_version") != 1 ||
                    !string.Equals(GetString(response, "panel_id"), panelId, StringComparison.Ordinal))
                {
                    throw new AuthenticationException("Relay returned an invalid authentication challenge.");
                }
                long expiresAtMs = GetLong(response, "expires_at_ms");
                if (expiresAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    throw new AuthenticationException("Relay authentication challenge has expired.");

                string challengeB64 = GetString(response, "challenge");
                try { challenge = Convert.FromBase64String(PadBase64(challengeB64)); }
                catch (FormatException ex) { throw new AuthenticationException("Relay authentication challenge was malformed.", ex); }
                if (challenge.Length != ChallengeBytes)
                    throw new AuthenticationException("Relay authentication challenge has an invalid length.");

                using (HMACSHA256 mac = new HMACSHA256(Encoding.ASCII.GetBytes(secret)))
                {
                    byte[] domain = Encoding.ASCII.GetBytes(AuthDomain);
                    byte[] panel = Encoding.UTF8.GetBytes(panelId);
                    byte[] material = new byte[domain.Length + panel.Length + 1 + challenge.Length];
                    Buffer.BlockCopy(domain, 0, material, 0, domain.Length);
                    Buffer.BlockCopy(panel, 0, material, domain.Length, panel.Length);
                    material[domain.Length + panel.Length] = 0;
                    Buffer.BlockCopy(challenge, 0, material, domain.Length + panel.Length + 1, challenge.Length);
                    byte[] proof = mac.ComputeHash(material);
                    SendJson(new Dictionary<string, object>
                    {
                        ["protocol_version"] = 1,
                        ["message_type"] = "panel_proof",
                        ["panel_id"] = panelId,
                        ["proof"] = Convert.ToBase64String(proof).TrimEnd('=')
                    });
                }

                response = ReadJsonBlocking(10000);
                if (string.Equals(GetString(response, "message_type"), "panel_authenticated", StringComparison.Ordinal) &&
                    GetInt(response, "protocol_version") == 1)
                {
                    long authenticatedUntil = GetLong(response, "expires_at_ms");
                    if (authenticatedUntil > 0 && authenticatedUntil <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        throw new AuthenticationException("Relay authentication session is already expired.");
                    ssl.ReadTimeout = Timeout.Infinite;
                    if (client?.Client != null) client.Client.ReceiveTimeout = 0;

                    // Relay transmits the onion address on every successful auth — fire the event
                    // so the panel can write it to the onion field and persist it immediately.
                    string onion = GetString(response, "onion_address");
                    if (!string.IsNullOrWhiteSpace(onion))
                        OnionAddressReceived?.Invoke(onion.Trim());

                    return;
                }
            }
            throw new AuthenticationException("Relay panel authentication failed.");
        }

        private void ReadLoop()
        {
            string reason = "relay connection closed";
            try
            {
                while (!stopping)
                {
                    Dictionary<string, object> message = ReadJsonBlocking(-1);
                    if (message == null) break;
                    string type = GetString(message, "message_type");
                    if (string.Equals(type, "telemetry", StringComparison.OrdinalIgnoreCase))
                    {
                        RelayTelemetryEvent telemetry = ParseTelemetry(message);
                        if (telemetry != null) TelemetryReceived?.Invoke(telemetry);
                    }
                    else if (string.Equals(type, "agent_command_result", StringComparison.OrdinalIgnoreCase))
                    {
                        RelayCommandResult result = ParseCommandResult(message);
                        if (result != null)
                        {
                            if (pending.TryGetValue(result.RequestId, out TaskCompletionSource<RelayCommandResult> completion))
                                completion.TrySetResult(result);
                            CommandResultReceived?.Invoke(result);
                        }
                    }
                    else if (string.Equals(type, "panel_pong", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep-alive acknowledgement.
                    }
                    else
                    {
                        // The relay currently defines only telemetry and control responses after auth.
                    }
                }
            }
            catch (EndOfStreamException) { reason = "relay closed the connection"; }
            catch (IOException ex) { reason = "relay I/O failure: " + ex.Message; }
            catch (SocketException ex) { reason = "relay socket failure: " + ex.Message; }
            catch (Exception ex) when (!(ex is ThreadAbortException)) { reason = "relay protocol failure: " + ex.Message; }
            finally
            {
                if (!stopping)
                {
                    stopping = true;
                    try { keepAliveTimer?.Dispose(); } catch { }
                    keepAliveTimer = null;
                    try { ssl?.Dispose(); } catch { }
                    try { client?.Close(); } catch { }
                    ssl = null;
                    client = null;
                    FailPending(reason);
                    Disconnected?.Invoke(reason);
                }
            }
        }

        private Dictionary<string, object> ReadJsonBlocking(int timeoutMilliseconds)
        {
            if (ssl == null) throw new IOException("Relay stream is unavailable.");
            if (timeoutMilliseconds > 0)
            {
                if (client?.Client != null)
                    client.Client.ReceiveTimeout = timeoutMilliseconds;
                ssl.ReadTimeout = timeoutMilliseconds;
            }
            byte[] header = ReadExact(4);
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
            if (length <= 0 || length > MaxFrameBytes)
                throw new InvalidDataException("Relay frame exceeds the allowed size.");
            byte[] payload = ReadExact(length);
            return serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(payload));
        }

        private byte[] ReadExact(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = ssl.Read(buffer, offset, count - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }

        private void SendJson(Dictionary<string, object> message)
        {
            lock (sendLock) SendJsonLocked(message);
        }

        private void SendJsonLocked(Dictionary<string, object> message)
        {
            if (ssl == null) throw new IOException("Relay stream is unavailable.");
            byte[] payload = Encoding.UTF8.GetBytes(serializer.Serialize(message));
            if (payload.Length == 0 || payload.Length > MaxFrameBytes)
                throw new InvalidDataException("Relay message exceeds the allowed size.");
            byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
            ssl.Write(header, 0, header.Length);
            ssl.Write(payload, 0, payload.Length);
            ssl.Flush();
        }

        private void SendKeepAlive(object state)
        {
            if (stopping || ssl == null) return;
            try
            {
                SendJson(new Dictionary<string, object>
                {
                    ["protocol_version"] = 1,
                    ["message_type"] = "panel_ping"
                });
            }
            catch (Exception ex)
            {
                Disconnect("relay keep-alive failed: " + ex.Message);
            }
        }

        private RelayTelemetryEvent ParseTelemetry(Dictionary<string, object> message)
        {
            string fingerprint = GetString(message, "fingerprint");
            if (fingerprint.Length != 64 || !IsHex(fingerprint)) return null;
            RelayTelemetryEvent value = new RelayTelemetryEvent
            {
                Fingerprint = fingerprint,
                TimestampMs = GetLong(message, "timestamp_ms"),
                SequenceNumber = GetLong(message, "sequence_number")
            };
            if (message.TryGetValue("telemetry", out object telemetryObject) && telemetryObject is Dictionary<string, object> telemetry)
            {
                foreach (KeyValuePair<string, object> item in telemetry)
                    value.Telemetry[item.Key] = item.Value == null ? string.Empty : Convert.ToString(item.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
            return value;
        }

        private static RelayCommandResult ParseCommandResult(Dictionary<string, object> message)
        {
            return new RelayCommandResult
            {
                RequestId = GetString(message, "request_id"),
                Status = GetString(message, "status"),
                Target = GetString(message, "target"),
                Queued = GetInt(message, "queued"),
                Dropped = GetInt(message, "dropped"),
                Detail = GetString(message, "detail")
            };
        }

        private bool ValidateServerCertificate(X509Certificate certificate, SslPolicyErrors errors, bool requireCertificateNameMatch)
        {
            if (certificate == null || caCertificate == null) return false;
            if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
            if (requireCertificateNameMatch && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;

            using (X509Chain chain = new X509Chain())
            using (X509Certificate2 server = new X509Certificate2(certificate))
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.ExtraStore.Add(caCertificate);
                if (!chain.Build(server)) return false;
                foreach (X509ChainElement element in chain.ChainElements)
                {
                    if (string.Equals(element.Certificate.Thumbprint, caCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        private static void ValidateSecret(string secret)
        {
            if (secret == null) throw new AuthenticationException("Relay authentication secret is required.");
            string compact = secret.Trim();
            if (compact.Length != 32) throw new AuthenticationException("Relay authentication secret must contain exactly 32 printable ASCII characters.");
            foreach (char character in compact)
            {
                if (character < 0x21 || character > 0x7e)
                    throw new AuthenticationException("Relay authentication secret must contain only printable ASCII characters.");
            }
        }

        private void FailPending(string reason)
        {
            foreach (KeyValuePair<string, TaskCompletionSource<RelayCommandResult>> item in pending)
            {
                item.Value.TrySetException(new IOException(reason));
                pending.TryRemove(item.Key, out _);
            }
        }

        private static string PadBase64(string value)
        {
            int remainder = (value ?? string.Empty).Length % 4;
            return value + new string('=', remainder == 0 ? 0 : 4 - remainder);
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }

        private static string GetString(Dictionary<string, object> values, string key)
        {
            return values != null && values.TryGetValue(key, out object value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        }

        private static int GetInt(Dictionary<string, object> values, string key)
        {
            if (values == null || !values.TryGetValue(key, out object value) || value == null) return 0;
            return int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out int result) ? result : 0;
        }

        private static long GetLong(Dictionary<string, object> values, string key)
        {
            if (values == null || !values.TryGetValue(key, out object value) || value == null) return 0;
            return long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out long result) ? result : 0;
        }
    }
}
