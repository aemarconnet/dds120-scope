using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Dds120.Display;

namespace Dds120.Droid.Screen
{
    // The scope screen as an Android View. The shared ScopeRenderer draws it and handles marker and
    // cursor drags; this adds the gestures:
    //   one finger on a marker or cursor .... drag it
    //   one finger elsewhere ................ pan (locked to the first direction): sideways moves the
    //                                         trigger point, up/down moves the trace under the finger
    //   two fingers ......................... pinch: side by side = time/div, one above the other =
    //                                         volts/div of the trace between them, diagonal = both;
    //                                         moving both fingers together pans the other axis
    //   double tap .......................... run / stop
    //   long press .......................... menu for that point
    // Pinches preview continuously and snap to the nearest real step when the fingers lift.
    internal sealed class ScopeView : View
    {
        enum Mode { Idle, Marker, Pending, Pan, Pinch }

        const float MinPinchSpanDp = 30;

        readonly ScopeRenderer renderer = new ScopeRenderer();
        readonly AndroidCanvas canvas = new AndroidCanvas();
        readonly GestureDetector gestures;
        readonly float touchSlop;
        Mode mode = Mode.Idle;
        float downX, downY, lastX, lastY;
        bool panSideways;
        int panTrace;

        // pinch state
        float spanX0, spanY0, focusX, focusY;
        bool zoomTime, zoomVolts;
        int zoomTrace;
        double timeScale = 1, voltScale = 1;

        public ScopeRenderer Renderer { get { return renderer; } }

        public event Action Tapped;
        public event Action DoubleTapped;
        public event Action<float, float> LongPressed;
        public event Action<double> PannedSideways;          // divisions
        public event Action<int, double> PannedVertically;   // trace, divisions
        public event Action<double> ZoomedHorizontally;      // factor, >1 magnifies
        public event Action<int, double> ZoomedVertically;   // trace, factor

        public ScopeView(Context context) : base(context)
        {
            renderer.Density = context.Resources.DisplayMetrics.Density;
            touchSlop = ViewConfiguration.Get(context).ScaledTouchSlop;
            gestures = new GestureDetector(context, new Listener(this));
            gestures.IsLongpressEnabled = true;
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            renderer.Width = w;
            renderer.Height = h;
        }

        protected override void OnDraw(Canvas c)
        {
            canvas.Canvas = c;
            renderer.Draw(canvas);
            canvas.Canvas = null;
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            if (mode != Mode.Pinch)
                gestures.OnTouchEvent(e);
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    downX = lastX = e.GetX();
                    downY = lastY = e.GetY();
                    if (renderer.TouchDown(downX, downY))
                    {
                        mode = Mode.Marker;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        Invalidate();
                    }
                    else
                    {
                        mode = renderer.InGrid(downX, downY) ? Mode.Pending : Mode.Idle;
                    }
                    break;

                case MotionEventActions.PointerDown:
                    if (e.PointerCount == 2)
                        BeginPinch(e);
                    break;

                case MotionEventActions.Move:
                    if (mode == Mode.Pinch && e.PointerCount >= 2)
                        UpdatePinch(e);
                    else if (mode == Mode.Marker)
                        renderer.TouchMove(e.GetX(), e.GetY());
                    else if (mode == Mode.Pending || mode == Mode.Pan)
                        UpdatePan(e.GetX(), e.GetY());
                    break;

                case MotionEventActions.PointerUp:
                    if (mode == Mode.Pinch)
                        EndPinch();
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (mode == Mode.Pinch)
                        EndPinch();
                    renderer.TouchUp();
                    mode = Mode.Idle;
                    Invalidate();
                    break;
            }
            return true;
        }

        void UpdatePan(float x, float y)
        {
            if (mode == Mode.Pending)
            {
                float dx = x - downX, dy = y - downY;
                if (Math.Abs(dx) < touchSlop && Math.Abs(dy) < touchSlop)
                    return;
                mode = Mode.Pan;
                panSideways = Math.Abs(dx) > Math.Abs(dy);
                panTrace = renderer.TraceNear(downX, downY);
                lastX = downX;
                lastY = downY;
                Parent?.RequestDisallowInterceptTouchEvent(true);
            }
            if (panSideways)
                PannedSideways?.Invoke((x - lastX) / renderer.DivWidth);
            else
                PannedVertically?.Invoke(panTrace, (lastY - y) / renderer.DivHeight);
            lastX = x;
            lastY = y;
        }

        void BeginPinch(MotionEvent e)
        {
            renderer.TouchUp(); // a second finger turns a marker drag or pan into a pinch
            mode = Mode.Pinch;
            Parent?.RequestDisallowInterceptTouchEvent(true);
            float x0 = e.GetX(0), y0 = e.GetY(0), x1 = e.GetX(1), y1 = e.GetY(1);
            spanX0 = Math.Abs(x1 - x0);
            spanY0 = Math.Abs(y1 - y0);
            focusX = (x0 + x1) / 2;
            focusY = (y0 + y1) / 2;
            // Fingers within ~60° of horizontal zoom time, within ~60° of vertical zoom volts.
            float min = MinPinchSpanDp * renderer.Density;
            zoomTime = spanX0 > min && spanX0 > spanY0 * 0.58f;
            zoomVolts = spanY0 > min && spanY0 > spanX0 * 0.58f;
            zoomTrace = renderer.TraceNear(focusX, focusY);
            timeScale = voltScale = 1;
        }

        void UpdatePinch(MotionEvent e)
        {
            float x0 = e.GetX(0), y0 = e.GetY(0), x1 = e.GetX(1), y1 = e.GetY(1);
            float fx = (x0 + x1) / 2, fy = (y0 + y1) / 2;
            // An axis that is not being zoomed follows both fingers as a pan.
            if (zoomTime)
                timeScale = Clamp(Math.Abs(x1 - x0) / spanX0, 0.05, 20);
            else
                PannedSideways?.Invoke((fx - focusX) / renderer.DivWidth);
            if (zoomVolts)
                voltScale = Clamp(Math.Abs(y1 - y0) / spanY0, 0.05, 20);
            else
                PannedVertically?.Invoke(zoomTrace, (focusY - fy) / renderer.DivHeight);
            focusX = fx;
            focusY = fy;
            ShowPreview();
        }

        void ShowPreview()
        {
            double[] v = { 1, 1, 1 };
            double time = 1;
            if (renderer.Settings != null && renderer.Settings.XYActive)
            {
                v[0] = timeScale;  // CH1 is the X axis
                v[1] = voltScale;
            }
            else
            {
                time = timeScale;
                v[zoomTrace] = voltScale;
            }
            renderer.SetPreview(time, v[0], v[1], v[2]);
            Invalidate();
        }

        void EndPinch()
        {
            mode = Mode.Idle;
            if (zoomTime && Math.Abs(Math.Log(timeScale)) > 0.05)
                ZoomedHorizontally?.Invoke(timeScale);
            if (zoomVolts && Math.Abs(Math.Log(voltScale)) > 0.05)
                ZoomedVertically?.Invoke(zoomTrace, voltScale);
            timeScale = voltScale = 1;
            renderer.ClearPreview();
            Invalidate();
        }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        sealed class Listener : GestureDetector.SimpleOnGestureListener
        {
            readonly ScopeView view;

            public Listener(ScopeView view)
            {
                this.view = view;
            }

            public override bool OnDown(MotionEvent e)
            {
                return true;
            }

            public override bool OnSingleTapConfirmed(MotionEvent e)
            {
                view.Tapped?.Invoke();
                return true;
            }

            public override bool OnDoubleTap(MotionEvent e)
            {
                view.DoubleTapped?.Invoke();
                return true;
            }

            public override void OnLongPress(MotionEvent e)
            {
                // only on an empty spot that has not started a drag
                if (view.mode != Mode.Pending)
                    return;
                view.mode = Mode.Idle;
                view.PerformHapticFeedback(FeedbackConstants.LongPress);
                view.LongPressed?.Invoke(e.GetX(), e.GetY());
            }
        }
    }
}
