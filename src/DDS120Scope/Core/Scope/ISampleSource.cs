using System;

namespace Dds120.Scope
{
    // What the acquisition engine needs from a scope: the DDS120 itself or the built-in demo.
    public interface ISampleSource : IDisposable
    {
        string Description { get; }
        bool IsSimulated { get; }

        void SetGain(int channel, byte hardwareGain);
        void SetSampleRate(byte rateCode);
        void SetChannelCount(int channels);
        void SetCoupling(bool ch1Dc, bool ch2Dc);
        void SetCalibrationFrequency(int hz);
        void Start();
        void Stop();

        // Reads raw ADC bytes (CH1/CH2 interleaved in two-channel mode). Returns bytes received.
        int Read(byte[] buffer, int count, uint timeoutMs);
        void AbortRead();
    }
}
