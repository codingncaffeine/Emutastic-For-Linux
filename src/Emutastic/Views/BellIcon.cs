using Avalonia.Media;

namespace Emutastic.Views
{
    /// <summary>
    /// Material Design bell / bell-off geometries (24×24 viewport) shared by the friend-detail
    /// header and the brief-card toggle. Upstream used MaterialDesign PackIcon Kind=Bell/BellOff —
    /// tintable vectors; the port's first cut used emoji glyphs, which color-emoji fonts refuse to
    /// tint on Linux (the gold hover never showed). Parsed once.
    /// </summary>
    internal static class BellIcon
    {
        public static readonly Geometry On = Geometry.Parse(
            "M21,19V20H3V19L5,17V11C5,7.9 7.03,5.17 10,4.29C10,4.19 10,4.1 10,4A2,2 0 0,1 12,2" +
            "A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V17L21,19M14,21" +
            "A2,2 0 0,1 12,23A2,2 0 0,1 10,21H14Z");

        public static readonly Geometry Off = Geometry.Parse(
            "M14,21A2,2 0 0,1 12,23A2,2 0 0,1 10,21H14M21,19V20H3V19L5,17V11C5,9.86 5.27,8.78 " +
            "5.75,7.83L7.27,9.35C7.09,9.87 7,10.42 7,11V18H17V16.08L19.25,18.33L21,19M12,6" +
            "A5,5 0 0,1 17,11V13.18L8.94,5.12C9.26,4.93 9.6,4.76 9.96,4.62C10.13,3.13 11,2 12,2" +
            "A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V11.18L17,9.18V11" +
            "A5,5 0 0,0 12,6M3.28,4L20,20.72L18.73,22L2,5.27L3.28,4Z");
    }
}
