using System;
using System.Windows.Forms;
using Dds120.Display;

namespace Dds120.UI
{
    // The scope screen as a WinForms control. The shared ScopeRenderer draws it and handles marker and
    // cursor drags; this adds the mouse:
    //   drag a marker or cursor ...... move it
    //   drag elsewhere on the grid ... pan (locked to the first direction): sideways moves the trigger
    //                                  point, up/down moves the trace under the pointer
    //   wheel ........................ time/div (a touchpad pinch arrives as Ctrl+wheel: the same)
    //   Shift+wheel .................. volts/div of the trace under the pointer
    //   double-click ................. run / stop
    //   right-click .................. menu for that point
    internal sealed class ScopeView : Control
    {
        enum Mode { Idle, Marker, Pending, Pan }

        readonly ScopeRenderer renderer = new ScopeRenderer();
        readonly GdiCanvas canvas = new GdiCanvas();
        Mode mode = Mode.Idle;
        int downX, downY, lastX, lastY;
        bool panSideways;
        int panTrace;

        public ScopeRenderer Renderer { get { return renderer; } }

        public event Action<int> TimebaseWheel;              // +1 slower, -1 faster
        public event Action<int, int> VoltsWheel;            // trace, +1 = more volts/div
        public event Action<double> PannedSideways;          // divisions
        public event Action<int, double> PannedVertically;   // trace, divisions
        public event Action DoubleClicked;
        public event Action<int, int> MenuRequested;         // client point

        public ScopeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Screen;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The paint surface knows the real DPI; Control.DeviceDpi can report 96 on .NET Framework.
            renderer.Density = e.Graphics.DpiX / 96f;
            renderer.Width = ClientSize.Width;
            renderer.Height = ClientSize.Height;
            canvas.Graphics = e.Graphics;
            renderer.Draw(canvas);
            canvas.Graphics = null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left)
                return;
            downX = lastX = e.X;
            downY = lastY = e.Y;
            if (renderer.TouchDown(e.X, e.Y))
            {
                mode = Mode.Marker;
                Capture = true;
                Invalidate();
            }
            else if (renderer.InGrid(e.X, e.Y))
            {
                mode = Mode.Pending;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            switch (mode)
            {
                case Mode.Marker:
                    renderer.TouchMove(e.X, e.Y);
                    return;
                case Mode.Pending:
                case Mode.Pan:
                    Pan(e.X, e.Y);
                    return;
            }
            ScopeRenderer.Drag over = renderer.HitTestAt(e.X, e.Y);
            bool sideways = over == ScopeRenderer.Drag.TriggerPosition || over == ScopeRenderer.Drag.CursorX1 || over == ScopeRenderer.Drag.CursorX2;
            Cursor = sideways ? Cursors.SizeWE : over != ScopeRenderer.Drag.None ? Cursors.SizeNS
                : renderer.InGrid(e.X, e.Y) ? Cursors.SizeAll : Cursors.Default;
        }

        void Pan(int x, int y)
        {
            if (mode == Mode.Pending)
            {
                int slop = (int)(4 * renderer.Density);
                if (Math.Abs(x - downX) < slop && Math.Abs(y - downY) < slop)
                    return;
                mode = Mode.Pan;
                panSideways = Math.Abs(x - downX) > Math.Abs(y - downY);
                panTrace = renderer.TraceNear(downX, downY);
            }
            if (panSideways)
                PannedSideways?.Invoke((x - lastX) / renderer.DivWidth);
            else
                PannedVertically?.Invoke(panTrace, (lastY - y) / renderer.DivHeight);
            lastX = x;
            lastY = y;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && renderer.InGrid(e.X, e.Y))
            {
                MenuRequested?.Invoke(e.X, e.Y);
                return;
            }
            if (mode != Mode.Idle)
            {
                renderer.TouchUp();
                mode = Mode.Idle;
                Capture = false;
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && renderer.InGrid(e.X, e.Y) &&
                renderer.HitTestAt(e.X, e.Y) == ScopeRenderer.Drag.None)
                DoubleClicked?.Invoke();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!renderer.InGrid(e.X, e.Y) || e.Delta == 0)
                return;
            int step = e.Delta < 0 ? 1 : -1;
            if ((ModifierKeys & Keys.Shift) != 0)
                VoltsWheel?.Invoke(renderer.TraceNear(e.X, e.Y), step);
            else
                TimebaseWheel?.Invoke(step);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                canvas.Dispose();
            base.Dispose(disposing);
        }
    }
}
