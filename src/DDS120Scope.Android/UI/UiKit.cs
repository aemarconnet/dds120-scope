using System;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Dds120.Display;

namespace Dds120.Droid.UI
{
    // Compact dark controls for the side panel, built in code (no layout XML).
    internal static class UiKit
    {
        public const float RowHeightDp = 40;

        public static int Dp(Context c, float dp)
        {
            return (int)Math.Round(dp * c.Resources.DisplayMetrics.Density);
        }

        public static Button Button(Context c, string text, Action click)
        {
            var b = new Button(c) { Text = text };
            b.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
            b.SetAllCaps(false);
            b.SetSingleLine(true);
            b.Ellipsize = TextUtils.TruncateAt.End;
            b.SetMinHeight(0);
            b.SetMinimumHeight(0);
            b.SetMinWidth(0);
            b.SetMinimumWidth(0);
            b.SetPadding(Dp(c, 4), 0, Dp(c, 4), 0);
            b.StateListAnimator = null; // no Material elevation shadow
            Style(b, Palette.Control, Palette.Text);
            b.Click += (s, e) => click();
            return b;
        }

        // Fill and text color; pressed feedback is a ripple over the fill.
        public static void Style(Button b, int fill, int textColor)
        {
            Context c = b.Context;
            var shape = new GradientDrawable();
            shape.SetCornerRadius(Dp(c, 6));
            shape.SetColor(fill);
            shape.SetStroke(Math.Max(1, Dp(c, 1)), new Color(fill == Palette.Control ? Palette.ControlBorder : fill));
            b.Background = new RippleDrawable(ColorStateList.ValueOf(new Color(0x40FFFFFF)), shape, null);
            b.SetTextColor(new Color(textColor));
        }

        // Children share the width equally (or by the given weights).
        public static LinearLayout Row(Context c, params View[] views)
        {
            return WeightedRow(c, null, views);
        }

        public static LinearLayout WeightedRow(Context c, float[] weights, params View[] views)
        {
            var row = new LinearLayout(c) { Orientation = Android.Widget.Orientation.Horizontal };
            int m = Dp(c, 2);
            for (int i = 0; i < views.Length; i++)
            {
                var lp = new LinearLayout.LayoutParams(0, Dp(c, RowHeightDp), weights != null ? weights[i] : 1f);
                lp.SetMargins(m, m, m, m);
                row.AddView(views[i], lp);
            }
            return row;
        }

        public static TextView Header(Context c, string text, int color)
        {
            var t = new TextView(c) { Text = text };
            t.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
            t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
            t.SetTextColor(new Color(color));
            t.SetPadding(Dp(c, 4), Dp(c, 10), Dp(c, 4), Dp(c, 2));
            t.SetSingleLine(true);
            return t;
        }
    }
}
