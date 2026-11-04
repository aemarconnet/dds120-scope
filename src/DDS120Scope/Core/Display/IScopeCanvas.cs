namespace Dds120.Display
{
    public enum FontKind { Sans, SansBold, Mono }
    public enum TextAlign { Left, Center, Right }

    // What ScopeRenderer needs from a drawing surface. Colors are 0xAARRGGBB, sizes in pixels.
    // The Windows app implements it on GDI+, the Android app on an Android Canvas.
    public interface IScopeCanvas
    {
        void Clear(int color);
        void Line(float x0, float y0, float x1, float y1, int color, float width);
        // Independent segments, x0,y0,x1,y1 each; count is the number of floats used.
        void Segments(float[] xy, int count, int color, float width, bool antialias);
        void Dots(float[] xy, int count, int color, float diameter);
        void FillPolygon(float[] xy, int count, int color);
        void FillRoundRect(float left, float top, float right, float bottom, float radius, int color);
        void StrokeRect(float left, float top, float right, float bottom, int color, float width);
        float TextWidth(string text, FontKind font, float size);
        void Text(string text, float x, float centerY, FontKind font, float size, int color, TextAlign align);
        void Clip(float left, float top, float right, float bottom);
        void Unclip();
        // A w×h ARGB image (row by row, not premultiplied) stretched over the rectangle.
        void Image(int[] argb, int w, int h, float left, float top, float right, float bottom);
    }

    // The app's colors as ARGB.
    public static class Palette
    {
        public const int Window = unchecked((int)0xFF181A1F);
        public const int Panel = unchecked((int)0xFF20232A);
        public const int PanelHeader = unchecked((int)0xFF2C3039);
        public const int Control = unchecked((int)0xFF2E323C);
        public const int ControlBorder = unchecked((int)0xFF464C5A);
        public const int Text = unchecked((int)0xFFDEE2E9);
        public const int TextDim = unchecked((int)0xFF969DAA);

        public const int Screen = unchecked((int)0xFF080A0D);
        public const int Grid = unchecked((int)0xFF262E38);
        public const int GridAxis = unchecked((int)0xFF465262);

        public static readonly int[] Channel = { unchecked((int)0xFFFAD61E), unchecked((int)0xFF28D2F0) };
        public const int Math = unchecked((int)0xFFEC6ACF);
        public const int XY = unchecked((int)0xFF5CE08A);

        public const int Run = unchecked((int)0xFF2EA043);
        public const int Stop = unchecked((int)0xFFC83737);
        public const int Wait = unchecked((int)0xFFC88C1E);
        public const int Accent = unchecked((int)0xFF4682DC);
        public const int Warning = unchecked((int)0xFFF05050);
        public const int Black = unchecked((int)0xFF000000);
        public const int White = unchecked((int)0xFFFFFFFF);

        public static int WithAlpha(int color, int alpha)
        {
            return color & 0x00FFFFFF | alpha << 24;
        }
    }
}
