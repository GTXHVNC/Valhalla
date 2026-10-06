using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace Valhalla
{
    internal sealed class RelaySettings
    {
        public string RelayAddress { get; set; } = string.Empty;
        public int RelayPort { get; set; } = 443;
        public string PanelId { get; set; } = "panel-01";
        public string AuthenticationSecretProtected { get; set; } = string.Empty;
        public string CaCertificatePath { get; set; } = string.Empty;
        public string OnionAddress { get; set; } = string.Empty;

        /// <summary>0=Roaming, 1=Local, 2=Temp, 3=ProgramFiles, 4=ProgramData</summary>
        public int InstallDirectory { get; set; } = 1;

        /// <summary>Folder name appended to the selected install directory.</summary>
        public string FolderName { get; set; } = "Einherjar";

        [ScriptIgnore]
        public string AuthenticationSecret
        {
            get => Unprotect(AuthenticationSecretProtected);
            set => AuthenticationSecretProtected = Protect(value ?? string.Empty);
        }

        internal RelaySettings Clone()
        {
            return new RelaySettings
            {
                RelayAddress = RelayAddress ?? string.Empty,
                RelayPort = RelayPort,
                PanelId = PanelId ?? "panel-01",
                AuthenticationSecretProtected = AuthenticationSecretProtected ?? string.Empty,
                CaCertificatePath = CaCertificatePath ?? string.Empty,
                OnionAddress = OnionAddress ?? string.Empty,
                InstallDirectory = InstallDirectory,
                FolderName = string.IsNullOrEmpty(FolderName) ? "Einherjar" : FolderName
            };
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            byte[] clear = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                byte[] encrypted = Convert.FromBase64String(value);
                byte[] clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal static class RelaySettingsStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        private static string RootDirectory => ValhallaStorage.RootDirectory;
        private static string SettingsDirectory => Path.Combine(RootDirectory, "settings", "relay");
        private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
        private static string AuthDirectory => Path.Combine(RootDirectory, "auth");
        private static string CanonicalCertificatePath => Path.Combine(AuthDirectory, "ca.crt");
        private const string RelativeCertificatePath = "auth\\ca.crt";

        public static RelaySettings Load()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                if (!File.Exists(SettingsPath))
                    return new RelaySettings { CaCertificatePath = RelativeCertificatePath };

                RelaySettings settings = Serializer.Deserialize<RelaySettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                settings = settings ?? new RelaySettings();
                if (string.IsNullOrWhiteSpace(settings.CaCertificatePath))
                    settings.CaCertificatePath = RelativeCertificatePath;
                if (string.Equals(settings.FolderName, "Valhalla", StringComparison.OrdinalIgnoreCase))
                    settings.FolderName = "Einherjar";
                if (string.IsNullOrWhiteSpace(settings.FolderName))
                    settings.FolderName = "Einherjar";
                return settings;
            }
            catch
            {
                return new RelaySettings { CaCertificatePath = RelativeCertificatePath };
            }
        }

        public static void Save(RelaySettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Directory.CreateDirectory(SettingsDirectory);
            string tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, Serializer.Serialize(settings), new UTF8Encoding(false));
            if (File.Exists(SettingsPath))
                File.Replace(tempPath, SettingsPath, null);
            else
                File.Move(tempPath, SettingsPath);
        }

        public static string ResolveCertificatePath(RelaySettings settings)
        {
            string configured = settings == null ? string.Empty : settings.CaCertificatePath;
            if (string.IsNullOrWhiteSpace(configured))
                return CanonicalCertificatePath;
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(RootDirectory, configured.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        }

        public static string StoreCaCertificate(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Certificate file is required.", nameof(sourcePath));
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("CA certificate file was not found.", sourcePath);

            try
            {
                using (X509Certificate2 certificate = LoadCertificate(sourcePath))
                    ValidateCaCertificate(certificate);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The selected file is not a valid X.509 certificate.", ex);
            }

            Directory.CreateDirectory(AuthDirectory);
            string tempPath = CanonicalCertificatePath + ".tmp";
            File.Copy(sourcePath, tempPath, true);
            try
            {
                using (X509Certificate2 stored = LoadCertificate(tempPath))
                    ValidateCaCertificate(stored);

                if (File.Exists(CanonicalCertificatePath))
                    File.Replace(tempPath, CanonicalCertificatePath, null);
                else
                    File.Move(tempPath, CanonicalCertificatePath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            return RelativeCertificatePath;
        }

        public static X509Certificate2 LoadCertificate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Certificate path is required.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("CA certificate file was not found.", path);

            byte[] data = File.ReadAllBytes(path);
            string text = Encoding.ASCII.GetString(data);
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            int beginIndex = text.IndexOf(begin, StringComparison.Ordinal);
            if (beginIndex >= 0)
            {
                int contentStart = beginIndex + begin.Length;
                int endIndex = text.IndexOf(end, contentStart, StringComparison.Ordinal);
                if (endIndex <= contentStart)
                    throw new InvalidDataException("The CA certificate PEM block is incomplete.");

                string encoded = text.Substring(contentStart, endIndex - contentStart);
                encoded = new string(encoded.Where(character => !char.IsWhiteSpace(character)).ToArray());
                try
                {
                    return new X509Certificate2(Convert.FromBase64String(encoded));
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("The CA certificate PEM data is malformed.", ex);
                }
            }

            return new X509Certificate2(data);
        }

        public static void ValidateCaCertificate(X509Certificate2 certificate)
        {
            if (certificate == null || certificate.RawData == null || certificate.RawData.Length == 0)
                throw new InvalidDataException("The selected CA certificate contains no certificate data.");

            X509BasicConstraintsExtension basicConstraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault();
            if (basicConstraints == null || !basicConstraints.CertificateAuthority)
                throw new InvalidDataException("The selected certificate is not marked as a certificate authority (CA).");
        }

        public static bool HasUsableCertificate(RelaySettings settings, out string error)
        {
            string path = ResolveCertificatePath(settings);
            if (!File.Exists(path))
            {
                error = "CA certificate file is missing: " + path;
                return false;
            }
            try
            {
                using (X509Certificate2 certificate = LoadCertificate(path))
                    ValidateCaCertificate(certificate);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "CA certificate could not be loaded: " + ex.Message;
                return false;
            }
        }
    }
}
