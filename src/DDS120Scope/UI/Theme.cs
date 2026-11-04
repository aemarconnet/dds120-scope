using System.Drawing;

namespace Dds120.UI
{
    internal static class Theme
    {
        public static readonly Color Window = Color.FromArgb(24, 26, 31);
        public static readonly Color Panel = Color.FromArgb(32, 35, 42);
        public static readonly Color PanelHeader = Color.FromArgb(44, 48, 57);
        public static readonly Color Control = Color.FromArgb(46, 50, 60);
        public static readonly Color ControlBorder = Color.FromArgb(70, 76, 90);
        public static readonly Color Text = Color.FromArgb(222, 226, 233);
        public static readonly Color TextDim = Color.FromArgb(150, 157, 170);

        public static readonly Color Screen = Color.FromArgb(8, 10, 13);
        public static readonly Color Grid = Color.FromArgb(38, 46, 56);
        public static readonly Color GridAxis = Color.FromArgb(70, 82, 98);

        public static readonly Color[] Channel = { Color.FromArgb(250, 214, 30), Color.FromArgb(40, 210, 240) };
        public static readonly Color Math = Color.FromArgb(Display.Palette.Math);

        public static readonly Color Run = Color.FromArgb(46, 160, 67);
        public static readonly Color Stop = Color.FromArgb(200, 55, 55);
        public static readonly Color Wait = Color.FromArgb(200, 140, 30);
        public static readonly Color Accent = Color.FromArgb(70, 130, 220);
        public static readonly Color Warning = Color.FromArgb(240, 80, 80);
    }
}
