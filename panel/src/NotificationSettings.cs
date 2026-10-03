using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Valhalla
{
    public sealed class NotificationSettings
    {
        public bool WindowsNotificationsEnabled { get; set; }

        public bool TelegramNotificationsEnabled { get; set; }

        public string TelegramBotTokenProtected { get; set; } = string.Empty;

        public string TelegramChatId { get; set; } = string.Empty;

        [ScriptIgnore]
        public string TelegramBotToken
        {
            get => NotificationSettingsStore.UnprotectString(TelegramBotTokenProtected);
            set => TelegramBotTokenProtected = NotificationSettingsStore.ProtectString(value ?? string.Empty);
        }
    }

    internal static class NotificationSettingsStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        private static string RootDirectory
            => ValhallaStorage.RootDirectory;

        private static string SettingsDirectory
            => Path.Combine(RootDirectory, "settings", "notifications");

        private static string SettingsPath
            => Path.Combine(SettingsDirectory, "notifications.json");

        public static NotificationSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new NotificationSettings();

                using (FileStream stream = File.OpenRead(SettingsPath))
                {
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        NotificationSettings settings = Serializer.Deserialize<NotificationSettings>(reader.ReadToEnd());
                        return settings ?? new NotificationSettings();
                    }
                }
            }
            catch
            {
                return new NotificationSettings();
            }
        }

        public static void Save(NotificationSettings settings)
        {
            if (settings == null)
                return;

            try
            {
                Directory.CreateDirectory(SettingsDirectory);

                string tempPath = SettingsPath + ".tmp";
                string json = Serializer.Serialize(settings);
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));

                if (File.Exists(SettingsPath))
                    File.Replace(tempPath, SettingsPath, null);
                else
                    File.Move(tempPath, SettingsPath);
            }
            catch
            {
                try
                {
                    string tempPath = SettingsPath + ".tmp";
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        internal static string ProtectString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            byte[] clear = Encoding.UTF8.GetBytes(value);
            byte[] ciphertext = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(ciphertext);
        }

        internal static string UnprotectString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            try
            {
                byte[] ciphertext = Convert.FromBase64String(value);
                byte[] clear = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal static class ValhallaStorage
    {
        public static string RootDirectory
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Valhalla");

        public static void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);
                Directory.CreateDirectory(Path.Combine(RootDirectory, "settings", "notifications"));
            }
            catch
            {
            }
        }
    }
}
