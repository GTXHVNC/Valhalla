using System.Drawing;

namespace Valhalla
{
    /// <summary>
    /// Application-level visual tokens that are not owned by a third-party skin.
    /// DevExpress skin tokens are kept in App.config because DevExpress consumes
    /// those values independently at startup.
    /// </summary>
    internal static class UiTheme
    {
        public const int AccentRed = 80;
        public const int AccentGreen = 192;
        public const int AccentBlue = 144;
        public const string AccentHex = "#50C090";
        public const string AccentRgb = "rgb(80, 192, 144)";

        public static readonly Color AccentColor = Color.FromArgb(AccentRed, AccentGreen, AccentBlue);
    }
}
