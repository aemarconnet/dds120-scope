using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Android.Hardware.Usb;
using Android.Runtime;
using Dds120.Scope;
using Dds120.Usb;
using Java.Nio;

namespace Dds120.Droid.Usb
{
    // One connected DDS120 with scope firmware running, driven through Android's USB host API.
    // Same protocol as Core/Scope/Dds120Device.cs: vendor OUT requests 0xE0..0xE6 with one data byte,
    // samples from bulk IN endpoint 0x86. Used from the acquisition thread only; AbortRead may be
    // called from any thread.
    public sealed class AndroidDds120Source : ISampleSource
    {
        const int VendorOut = 0x40;   // bmRequestType: host-to-device, vendor, device
        const byte ReqGainCh1 = 0xE0;
        const byte ReqGainCh2 = 0xE1;
        const byte ReqSampleRate = 0xE2;
        const byte ReqStartStop = 0xE3;
        const byte ReqNumChannels = 0xE4;
        const byte ReqCoupling = 0xE5;
        const byte ReqCalFreq = 0xE6;
        const int ControlTimeoutMs = 1000;
        const int ENODEV = 19, EIO = 5;

        // Reads keep several requests queued so the FX2's small FIFO never waits for the phone.
        // 16 KB is the most a single request may carry on Android before 9.
        const int RequestSize = 16384;
        const int RequestCount = 16;
        const int PollMs = 50;

        // Set when queued requests misbehave on this phone; later connections then use bulkTransfer.
        static volatile bool queuedReadsBroken;

        readonly UsbDeviceConnection conn;
        readonly UsbInterface intf;
        readonly UsbEndpoint endpoint;
        readonly Action<string> log;
        readonly Slot[] slots;
        byte[] syncChunk;
        volatile bool aborted;

        sealed class Slot
        {
            public UsbRequest Request;
            public ByteBuffer Buffer;
            public IntPtr Address;
            public int Length;
            public long Seq;
            public bool Done;
            public int Received;
        }

        public string Description { get; }
        public bool IsSimulated { get { return false; } }

        AndroidDds120Source(UsbDeviceConnection conn, UsbInterface intf, UsbEndpoint endpoint, string description, Action<string> log)
        {
            this.conn = conn;
            this.intf = intf;
            this.endpoint = endpoint;
            this.log = log;
            Description = description;
            slots = new Slot[RequestCount];
        }

        // Takes ownership of conn: claims the interface with the sample endpoint, stops the
        // firmware and drains anything left in the FIFO.
        public static AndroidDds120Source Open(UsbDevice dev, UsbDeviceConnection conn, string description, Action<string> log)
        {
            UsbInterface intf = null;
            UsbEndpoint ep = null;
            for (int i = 0; i < dev.InterfaceCount && ep == null; i++)
            {
                UsbInterface candidate = dev.GetInterface(i);
                for (int j = 0; j < candidate.EndpointCount; j++)
                {
                    UsbEndpoint e = candidate.GetEndpoint(j);
                    if ((int)e.Address == Dds120Device.EndpointIn)
                    {
                        intf = candidate;
                        ep = e;
                        break;
                    }
                }
            }
            if (ep == null)
            {
                conn.Close();
                throw new DeviceNotReadyException("The scope firmware has no sample endpoint 0x86. Unplug and replug the scope.");
            }
            if (!conn.ClaimInterface(intf, true))
            {
                conn.Close();
                throw new DeviceNotReadyException("Cannot claim the scope's USB interface (another app may be using it).");
            }
            if (intf.AlternateSetting != 0)
                conn.SetInterface(intf);

            var scope = new AndroidDds120Source(conn, intf, ep, description, log);
            try
            {
                scope.Stop();
                scope.Drain();
            }
            catch
            {
                scope.Dispose();
                throw;
            }
            return scope;
        }

        void Write(byte request, byte value)
        {
            int r = conn.ControlTransfer((UsbAddressing)VendorOut, request, 0, 0, new[] { value }, 1, ControlTimeoutMs);
            if (r < 0)
                throw new UsbException("Vendor request 0x" + request.ToString("X2"), ENODEV);
        }

        public void SetGain(int channel, byte hardwareGain)
        {
            Write(channel == 0 ? ReqGainCh1 : ReqGainCh2, hardwareGain);
        }

        public void SetSampleRate(byte rateCode)
        {
            Write(ReqSampleRate, rateCode);
        }

        public void SetChannelCount(int channels)
        {
            Write(ReqNumChannels, (byte)channels);
        }

        public void SetCoupling(bool ch1Dc, bool ch2Dc)
        {
            Write(ReqCoupling, (byte)((ch1Dc ? 0x01 : 0) | (ch2Dc ? 0x10 : 0)));
        }

        public void SetCalibrationFrequency(int hz)
        {
            Write(ReqCalFreq, Dds120Model.CalFrequencyCode(hz));
        }

        public void Start()
        {
            aborted = false;
            Write(ReqStartStop, 1);
        }

        public void Stop()
        {
            Write(ReqStartStop, 0);
        }

        public void AbortRead()
        {
            aborted = true;
        }

        // Discards whatever the FIFO still holds.
        void Drain()
        {
            var tmp = new byte[RequestSize];
            for (int i = 0; i < 64; i++)
                if (conn.BulkTransfer(endpoint, tmp, 0, tmp.Length, 20) <= 0)
                    break;
        }

        // Reads exactly count bytes (or fewer on timeout or abort) from the sample endpoint.
        public int Read(byte[] buffer, int count, uint timeoutMs)
        {
            if (!queuedReadsBroken)
            {
                try
                {
                    return ReadQueued(buffer, count, timeoutMs);
                }
                catch (Java.Lang.Exception ex)
                {
                    // The connection's request state is unknown now: drop it and reconnect with plain
                    // bulkTransfer reads.
                    queuedReadsBroken = true;
                    log?.Invoke("Queued USB reads failed (" + ex.Message + "); switching to bulkTransfer");
                    throw new UsbException("Bulk read", EIO);
                }
            }
            return ReadSync(buffer, count, timeoutMs);
        }

        int ReadQueued(byte[] buffer, int count, uint timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var free = new Stack<Slot>();
            var inFlight = new List<Slot>();
            for (int i = slots.Length - 1; i >= 0; i--)
            {
                if (slots[i] == null)
                    slots[i] = NewSlot();
                free.Push(slots[i]);
            }

            int total = 0;          // bytes delivered to buffer, in stream order
            long queued = 0;        // bytes requested by the requests in flight
            long nextSeq = 0, nextDeliver = 0;
            while (true)
            {
                while (free.Count > 0 && total + queued < count && !aborted)
                {
                    Slot s = free.Pop();
                    s.Length = (int)Math.Min(RequestSize, count - total - queued);
                    s.Buffer.Clear();
                    s.Buffer.Limit(s.Length);
                    s.Seq = nextSeq++;
                    s.Done = false;
                    s.Received = 0;
                    if (!s.Request.Queue(s.Buffer))
                        throw new Java.Lang.IllegalStateException("UsbRequest.queue returned false");
                    inFlight.Add(s);
                    queued += s.Length;
                }
                if (inFlight.Count == 0)
                    break;
                if (aborted || sw.ElapsedMilliseconds >= timeoutMs)
                {
                    CancelAll(inFlight);
                    break;
                }

                UsbRequest r;
                try
                {
                    r = conn.RequestWait(Math.Min(PollMs, Math.Max(1, timeoutMs - sw.ElapsedMilliseconds)));
                }
                catch (Java.Util.Concurrent.TimeoutException)
                {
                    continue;
                }
                if (r == null)
                    throw new UsbException("Bulk read", ENODEV); // the device went away
                Slot done = Find(inFlight, r);
                if (done == null)
                    throw new Java.Lang.IllegalStateException("requestWait returned an unknown request");
                done.Done = true;
                done.Received = Math.Min(done.Length, done.Buffer.Position());

                // Requests on one endpoint complete in order, but deliver strictly by sequence anyway.
                Slot head;
                while ((head = inFlight.Find(x => x.Seq == nextDeliver)) != null && head.Done)
                {
                    if (head.Received > 0)
                        Marshal.Copy(head.Address, buffer, total, head.Received);
                    total += head.Received;
                    queued -= head.Length;
                    nextDeliver++;
                    inFlight.Remove(head);
                    free.Push(head);
                }
            }
            return total;
        }

        Slot NewSlot()
        {
            var r = new UsbRequest();
            if (!r.Initialize(conn, endpoint))
                throw new Java.Lang.IllegalStateException("UsbRequest.initialize failed");
            ByteBuffer b = ByteBuffer.AllocateDirect(RequestSize);
            return new Slot { Request = r, Buffer = b, Address = JNIEnv.GetDirectBufferAddress(b.Handle) };
        }

        static Slot Find(List<Slot> inFlight, UsbRequest r)
        {
            foreach (Slot s in inFlight)
                if (ReferenceEquals(s.Request, r) || JNIEnv.IsSameObject(s.Request.Handle, r.Handle))
                    return s;
            return null;
        }

        // Cancels the requests still in flight and waits until the system hands them all back, so the
        // slots can be reused. Their data is dropped: the caller restarts the capture anyway.
        void CancelAll(List<Slot> inFlight)
        {
            foreach (Slot s in inFlight)
                if (!s.Done)
                    s.Request.Cancel();
            var sw = Stopwatch.StartNew();
            while (inFlight.Exists(x => !x.Done))
            {
                if (sw.ElapsedMilliseconds > 2000)
                    throw new Java.Lang.IllegalStateException("cancelled USB requests did not complete");
                UsbRequest r;
                try
                {
                    r = conn.RequestWait(100);
                }
                catch (Java.Util.Concurrent.TimeoutException)
                {
                    continue;
                }
                if (r == null)
                    throw new UsbException("Bulk read", ENODEV);
                Slot s = Find(inFlight, r);
                if (s != null)
                    s.Done = true;
            }
            inFlight.Clear();
        }

        // Fallback: synchronous bulkTransfer calls. Android 9+ allows more than 16 KB per call, which
        // keeps the gaps between calls (where the FX2 FIFO can overflow) rare.
        int ReadSync(byte[] buffer, int count, uint timeoutMs)
        {
            int chunk = OperatingSystem.IsAndroidVersionAtLeast(28) ? 128 * 1024 : RequestSize;
            if (syncChunk == null || syncChunk.Length != chunk)
                syncChunk = new byte[chunk];
            var sw = Stopwatch.StartNew();
            int total = 0;
            while (total < count && !aborted)
            {
                long left = timeoutMs - sw.ElapsedMilliseconds;
                if (left <= 0)
                    break;
                int n = Math.Min(chunk, count - total);
                int got = conn.BulkTransfer(endpoint, syncChunk, 0, n, (int)Math.Min(left, 1000));
                if (got <= 0)
                    break; // timeout, or a gone device, which the next control request reports
                System.Buffer.BlockCopy(syncChunk, 0, buffer, total, got);
                total += got;
            }
            return total;
        }

        public void Dispose()
        {
            try
            {
                Stop();
            }
            catch (Exception)
            {
                // device may already be gone
            }
            foreach (Slot s in slots)
            {
                if (s == null)
                    continue;
                try { s.Request.Close(); } catch (Java.Lang.Exception) { }
                s.Buffer.Dispose();
            }
            try { conn.ReleaseInterface(intf); } catch (Java.Lang.Exception) { }
            conn.Close();
        }
    }
}
