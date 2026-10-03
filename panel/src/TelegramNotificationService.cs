using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Valhalla
{
    internal static class TelegramNotificationService
    {
        private static readonly Regex BotTokenPattern =
            new Regex(@"^\d+:[A-Za-z0-9_-]{35}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsValidBotToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && BotTokenPattern.IsMatch(token.Trim());
        }

        public static bool IsValidChatId(string chatId)
        {
            long parsed;
            return long.TryParse((chatId ?? string.Empty).Trim(), out parsed);
        }

        public static async Task SendConnectionMessageAsync(
            string botToken,
            string chatId,
            string clientName,
            string tag,
            string ipAddress,
            string country)
        {
            if (!IsValidBotToken(botToken))
                throw new ArgumentException("The Telegram Bot Token is not in the expected format.", "botToken");
            if (!IsValidChatId(chatId))
                throw new ArgumentException("The Telegram Chat ID must be a numeric value.", "chatId");

            string message = BuildConnectionMessage(clientName, tag, ipAddress, country);
            string encodedToken = botToken.Trim();
            string url = "https://api.telegram.org/bot" + encodedToken + "/sendMessage" +
                         "?chat_id=" + WebUtility.UrlEncode(chatId.Trim()) +
                         "&text=" + WebUtility.UrlEncode(message) +
                         "&parse_mode=Markdown";

            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                using (HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false))
                {
                    string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "Telegram API returned " + (int)response.StatusCode + " " +
                            response.StatusCode + ": " + responseBody);
                    }
                }
            }
        }

        public static string BuildConnectionMessage(string clientName, string tag, string ipAddress, string country)
        {
            string safeName = EscapeMarkdown(clientName, "Test");
            string safeTag = EscapeMarkdown(tag, "Valhalla");
            string safeIp = EscapeMarkdown(ipAddress, "127.0.0.1");
            string safeCountry = EscapeMarkdown(country, "Local");

            return "✨ *New Client Connected!* ✨\n" +
                   "👤 Name: " + safeName + "\n" +
                   "🔖 Tag: " + safeTag + "\n" +
                   "🌐 IP: " + safeIp + "\n" +
                   "🌎 Country: " + safeCountry;
        }

        private static string EscapeMarkdown(string value, string fallback)
        {
            string text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return text.Replace("\\", "\\\\")
                       .Replace("_", "\\_")
                       .Replace("*", "\\*")
                       .Replace("[", "\\[")
                       .Replace("]", "\\]")
                       .Replace("`", "\\`");
        }
    }
}
