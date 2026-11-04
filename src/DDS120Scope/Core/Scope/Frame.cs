using System;

namespace Dds120.Scope
{
    // One processed capture. Samples are volts at the probe tip (probe factor and inversion applied).
    public sealed class Frame
    {
        public ScopeSettings Settings;   // snapshot the frame was captured with
        public double SampleRate;        // effective, after averaging
        public float[][] Volts = new float[2][]; // null for a disabled channel
        // Peak detect: lowest and highest raw sample behind each displayed sample (else null).
        public float[][] MinEnvelope = new float[2][];
        public float[][] MaxEnvelope = new float[2][];
        public bool[] Clipped = new bool[2];
        public int SampleCount;
        public int Averaged;             // acquisitions averaged into this frame (Average mode), else 0

        // Display window: sample index (with sub-sample fraction) at the left edge of the screen.
        public double ScreenStart;
        public int ScreenSamples;
        public bool Triggered;
        public double TriggerIndex = double.NaN;  // sample index of the trigger point, if triggered
        public DateTime Time;

        public double SampleInterval { get { return 1.0 / SampleRate; } }
    }
}
