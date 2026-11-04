using System.Windows.Media;

namespace ClaudeNotch
{
    static class Theme
    {
        public static readonly Brush Black = Frozen(0x0B, 0x0B, 0x0B);
        public static readonly Brush Chip = Frozen(0x2A, 0x2A, 0x2A);
        public static readonly Brush ChipHover = Frozen(0x3A, 0x3A, 0x3A);
        public static readonly Brush Accent = Frozen(0x8F, 0x7B, 0xF0);
        public static readonly Brush Amber = Frozen(0xF5, 0xA5, 0x24);
        public static readonly Brush Green = Frozen(0x5B, 0xBF, 0x72);
        public static readonly Brush Blue = Frozen(0x6C, 0xA0, 0xF0);
        public static readonly Brush Dim = Frozen(0x9A, 0x9A, 0x9A);
        public static readonly Brush Raised = Frozen(0x1A, 0x1A, 0x1A); // a card on the card (quick voice answers)
        public static readonly Brush Line = Frozen(0x30, 0x30, 0x30);
        public static readonly Brush Idle = Frozen(0x70, 0x70, 0x70);
        public static readonly Brush Red = Frozen(0xE5, 0x6B, 0x6B);   // a value that went down
        public static readonly FontFamily Symbols = new FontFamily("Segoe UI Symbol, Segoe UI Emoji"); // weather
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
