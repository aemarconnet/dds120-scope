# DDS-120 hardware diagnostics over WinUSB via ctypes, for when a rebuilt Dds120Probe.exe cannot
# run (e.g. blocked by Smart App Control). The scope firmware must already be running (start
# DDS120Scope.exe once, or Dds120Probe load) and no other program may have the scope open.
#   py dds_diag.py <test> [key=value ...]
#   tests: map, stale, gainstep, rates, coupling, gains, dump
#   keys:  rate=<code> ch=1|2 cal=<Hz> sig=<byte index of the probed channel> gain=<1..20> ...
# Measurements use the scope's calibration output wired to an input.
import ctypes, ctypes.wintypes as wt, sys, time, statistics, uuid

EP = 0x86
k32 = ctypes.WinDLL('kernel32', use_last_error=True)
wu = ctypes.WinDLL('winusb', use_last_error=True)
cm = ctypes.WinDLL('cfgmgr32')


def find_path():
    guid = (ctypes.c_ubyte * 16).from_buffer_copy(uuid.UUID('a5dcbf10-6530-11d2-901f-00c04fb951ed').bytes_le)
    size = wt.ULONG()
    cm.CM_Get_Device_Interface_List_SizeW(ctypes.byref(size), ctypes.byref(guid), None, 0)
    buf = ctypes.create_unicode_buffer(size.value)
    cm.CM_Get_Device_Interface_ListW(ctypes.byref(guid), None, buf, size.value, 0)
    for p in ctypes.wstring_at(buf, size.value).split('\0'):
        if 'vid_8102&pid_8102' in p.lower():
            return p
    raise SystemExit('No DDS-120 (8102:8102) with a WinUSB driver found')


class Setup(ctypes.Structure):
    _pack_ = 1
    _fields_ = [('rt', ctypes.c_ubyte), ('req', ctypes.c_ubyte), ('val', ctypes.c_ushort),
                ('idx', ctypes.c_ushort), ('len', ctypes.c_ushort)]


k32.CreateFileW.restype = wt.HANDLE
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
k32.CloseHandle.argtypes = [wt.HANDLE]
wu.WinUsb_Initialize.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
wu.WinUsb_Free.argtypes = [ctypes.c_void_p]
wu.WinUsb_ControlTransfer.argtypes = [ctypes.c_void_p, Setup, ctypes.c_void_p, wt.ULONG,
                                      ctypes.POINTER(wt.ULONG), ctypes.c_void_p]
wu.WinUsb_ReadPipe.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, ctypes.c_void_p, wt.ULONG,
                               ctypes.POINTER(wt.ULONG), ctypes.c_void_p]
wu.WinUsb_SetPipePolicy.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, wt.ULONG, wt.ULONG, ctypes.c_void_p]
wu.WinUsb_FlushPipe.argtypes = [ctypes.c_void_p, ctypes.c_ubyte]
wu.WinUsb_GetDescriptor.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, ctypes.c_ubyte, ctypes.c_ushort,
                                    ctypes.c_void_p, wt.ULONG, ctypes.POINTER(wt.ULONG)]
for fn in (wu.WinUsb_Initialize, wu.WinUsb_Free, wu.WinUsb_ControlTransfer, wu.WinUsb_ReadPipe,
           wu.WinUsb_SetPipePolicy, wu.WinUsb_FlushPipe, wu.WinUsb_GetDescriptor):
    fn.restype = wt.BOOL


def check(ok, what):
    if not ok:
        raise OSError(ctypes.get_last_error(), what)


class Scope:
    def __init__(self):
        self.h = k32.CreateFileW(find_path(), 0xC0000000, 3, None, 3, 0x40000080, None)
        if self.h is None or self.h == wt.HANDLE(-1).value:
            err = ctypes.get_last_error()
            raise SystemExit('Cannot open the scope (error %d)%s' % (
                err, '; close DDS120Scope.exe first' if err == 5 else ''))
        self.ih = ctypes.c_void_p()
        check(wu.WinUsb_Initialize(self.h, ctypes.byref(self.ih)), 'WinUsb_Initialize')
        product = self.string(2)
        if product != 'fx2lafw':
            raise SystemExit('Scope firmware is not running (product %r); run Dds120Probe load first' % product)
        self.policy(0x02, 1, 1)  # AUTO_CLEAR_STALL

    def string(self, index):
        buf = (ctypes.c_ubyte * 255)()
        n = wt.ULONG()
        check(wu.WinUsb_GetDescriptor(self.ih, 3, index, 0x0409, buf, 255, ctypes.byref(n)), 'GetDescriptor')
        return bytes(buf[2:n.value]).decode('utf-16-le')

    def policy(self, kind, value, size=4):
        v = (ctypes.c_ubyte if size == 1 else wt.ULONG)(value)
        check(wu.WinUsb_SetPipePolicy(self.ih, EP, kind, size, ctypes.byref(v)), 'SetPipePolicy')

    def out(self, req, value):
        b = (ctypes.c_ubyte * 1)(value)
        n = wt.ULONG()
        check(wu.WinUsb_ControlTransfer(self.ih, Setup(0x40, req, 0, 0, 1), b, 1, ctypes.byref(n), None),
              'control 0x%02X' % req)

    def configure(self, rate, ch=2, gain1=1, gain2=1, dc1=True, dc2=True, cal=None):
        self.out(0xE0, gain1)
        self.out(0xE1, gain2)
        self.out(0xE5, (0x01 if dc1 else 0) | (0x10 if dc2 else 0))
        self.out(0xE4, ch)
        self.out(0xE2, rate)
        if cal is not None:
            self.out(0xE6, cal_code(cal))

    def start(self):
        self.out(0xE3, 1)

    def stop(self):
        self.out(0xE3, 0)

    def flush(self):
        check(wu.WinUsb_FlushPipe(self.ih, EP), 'FlushPipe')

    def read(self, count, timeout_ms=5000, buf=None):
        self.policy(0x03, timeout_ms)
        if buf is None:
            buf = (ctypes.c_ubyte * count)()
        total = 0
        while total < count:
            n = wt.ULONG()
            if not wu.WinUsb_ReadPipe(self.ih, EP, ctypes.byref(buf, total), count - total, ctypes.byref(n), None):
                err = ctypes.get_last_error()
                if err == 121:
                    break
                raise OSError(err, 'ReadPipe')
            if n.value == 0:
                break
            total += n.value
        return ctypes.string_at(buf, total)

    def close(self):
        try:
            self.stop()
        except OSError:
            pass
        wu.WinUsb_Free(self.ih)
        k32.CloseHandle(self.h)


def cal_code(hz):
    if hz < 1000:
        return 0 if hz == 100 else 100 + hz // 10
    return hz // 1000


RATES = {10: 100e3, 20: 200e3, 50: 500e3, 1: 1e6, 2: 2e6, 5: 5e6, 8: 8e6, 11: 10e6, 15: 15e6,
         24: 24e6, 30: 30e6, 48: 48e6}


def split(data, nch):
    return [data[c::nch] for c in range(nch)]


def edges(x):
    lo, hi = min(x), max(x)
    if hi - lo < 6:
        return None
    mid = (lo + hi) / 2
    band = max(1.5, (hi - lo) * 0.15)
    up, dn = [], []
    state = x[0] > mid
    for i, v in enumerate(x):
        if state and v < mid - band:
            state = False
            dn.append(i)
        elif not state and v > mid + band:
            state = True
            up.append(i)
    return lo, hi, up, dn


def describe(x, label, rate=None, cal=None, show_breaks=8):
    lo, hi = min(x), max(x)
    mean = sum(x) / len(x)
    s = '  %s: n=%d min=%d max=%d mean=%.2f' % (label, len(x), lo, hi, mean)
    e = edges(x)
    if e is None or len(e[2]) < 3:
        print(s + ' (no square wave)')
        return None
    lo, hi, up, dn = e
    hist = [0] * 256
    for v in x:
        hist[v] += 1
    low_level = max(range(lo, (lo + hi) // 2 + 1), key=lambda k: hist[k])
    high_level = max(range((lo + hi) // 2, hi + 1), key=lambda k: hist[k])
    iv = [b - a for a, b in zip(up, up[1:])]
    period = statistics.median(iv)
    highs = []
    for u in up:
        d = next((d for d in dn if d > u), None)
        if d is not None:
            highs.append(d - u)
    duty = statistics.median(highs) / period if highs else 0
    bad = [(up[i], iv[i]) for i in range(len(iv)) if abs(iv[i] - period) > max(2, period * 0.01)]
    s += ' levels %d/%d (p-p %d) period %.1f samples duty %.1f%% edges %d irregular %d' % (
        low_level, high_level, high_level - low_level, period, duty * 100, len(up), len(bad))
    if rate and cal:
        s += ' -> implied rate %.4g S/s (nominal %.4g)' % (period * cal, rate)
    print(s)
    if bad:
        print('    first irregular intervals (start index, length):', bad[:show_breaks])
    print('    first rising edges:', up[:6])
    return period, bad, up


def args(argv):
    d = {}
    for a in argv:
        k, v = a.split('=', 1)
        d[k] = int(v)
    return d


def test_map(sc, a):
    # CH1 gain 1, CH2 gain 2: the stream whose square wave doubles is the one 0xE1 controls.
    for g1, g2 in ((1, 1), (1, 2), (2, 1)):
        sc.configure(8, 2, g1, g2, cal=a.get('cal', 1000))
        sc.start()
        d = sc.read(1 << 20)
        sc.stop()
        print('2 channels, gain CH1 x%d CH2 x%d:' % (g1, g2))
        for c, x in enumerate(split(d[2560 * 2:], 2)):
            describe(x, 'byte %d' % c)
    sc.configure(8, 1, 1, 1, cal=a.get('cal', 1000))
    sc.start()
    d = sc.read(1 << 19)
    sc.stop()
    print('1 channel mode:')
    describe(d[2560:], 'stream')


def test_stale(sc, a):
    # Mimic the app: Start, read, pause for processing, Start again, read. Then look for the
    # point where the second read's data becomes continuous with the cal square wave.
    rate, nch, cal = a.get('rate', 8), a.get('ch', 2), a.get('cal', 10000)
    pause = a.get('pause', 50) / 1000.0
    size = a.get('bytes', 65536)
    sc.configure(rate, nch, a.get('gain', 1), a.get('gain', 1), cal=cal)
    sc.flush()
    for f in range(a.get('frames', 4)):
        sc.start()
        d = sc.read(size)
        print('frame %d (%d bytes):' % (f, len(d)))
        x = split(d, nch)[a.get('sig', 1) if nch == 2 else 0]
        describe(x, 'signal', RATES[rate], cal)
        time.sleep(pause)
    sc.stop()


def test_gainstep(sc, a):
    # Capture at one gain, change gain while streaming (as the app does), capture again: the
    # samples still showing the old amplitude are stale.
    nch, sig = a.get('ch', 2), a.get('sig', 1)
    sc.configure(a.get('rate', 8), nch, 1, 1, cal=a.get('cal', 1000))
    sc.start()
    sc.read(65536)
    for g in (2, 1, 2):
        time.sleep(a.get('pause', 50) / 1000.0)
        sc.out(0xE0, g)
        sc.out(0xE1, g)
        sc.start()
        d = sc.read(65536)
        x = split(d, nch)[sig if nch == 2 else 0]
        blk = 256
        means = ['%d-%d' % (min(x[i:i + blk]), max(x[i:i + blk])) for i in range(0, 6144, blk)]
        print('after switching to x%d, min-max per %d samples:' % (g, blk), ' '.join(means))
    sc.stop()


def test_rates(sc, a):
    nch, cal, sig = a.get('ch', 2), a.get('cal', 10000), a.get('sig', 1)
    seconds = a.get('ms', 60) / 1000.0
    codes = [c for c in RATES if c != a.get('skip', -1)]
    if 'only' in a:
        codes = [a['only']]
    sc.configure(10, nch, 1, 1, cal=cal)
    for code in codes:
        sc.out(0xE2, code)
        n = int(RATES[code] * nch * seconds) // 512 * 512 + 512 * 16
        sc.flush()
        sc.start()
        t = time.perf_counter()
        d = sc.read(n, 10000)
        dt = time.perf_counter() - t
        sc.stop()
        print('rate code %d (%.4g S/s, %d ch): %d of %d bytes in %.1f ms = %.1f MB/s (nominal %.1f MB/s)' % (
            code, RATES[code], nch, len(d), n, dt * 1000, len(d) / dt / 1e6, RATES[code] * nch / 1e6))
        x = split(d, nch)[sig if nch == 2 else 0][2560:]
        describe(x, 'signal', RATES[code], cal, 4)
        time.sleep(0.05)


def test_coupling(sc, a):
    nch, sig = 2, a.get('sig', 1)
    for dc in (True, False):
        sc.configure(a.get('rate', 1), nch, 1, 1, dc, dc, cal=a.get('cal', 1000))
        time.sleep(0.3)
        sc.flush()
        sc.start()
        d = sc.read(1 << 20)
        sc.stop()
        print('%s coupling:' % ('DC' if dc else 'AC'))
        for c, x in enumerate(split(d[2560 * 2:], 2)):
            describe(x, 'CH%d' % (c + 1))


def test_gains(sc, a):
    # AC coupling centres the cal square wave on 0 V, so higher gains fit before clipping.
    dc = bool(a.get('dc', 0))
    for g in (1, 2, 5, 10, 20):
        sc.configure(a.get('rate', 1), 2, g, g, dc, dc, cal=a.get('cal', 1000))
        time.sleep(0.4)
        sc.flush()
        sc.start()
        d = sc.read(1 << 19)
        sc.stop()
        print('gain x%d (%s):' % (g, 'DC' if dc else 'AC'))
        for c, x in enumerate(split(d[2560 * 2:], 2)):
            describe(x, 'CH%d' % (c + 1), show_breaks=0)


def test_throughput(sc, a):
    # Steady delivery rate per rate code, from two reads of different length (the difference
    # cancels the start-up latency). Below nominal means the scope samples slower than the code
    # says or drops data. Needs no signal, so it also covers 1-channel mode with CH1 open.
    nch = a.get('ch', 1)
    short, long_ = a.get('short', 100) / 1000.0, a.get('long', 400) / 1000.0
    codes = [a['only']] if 'only' in a else [c for c in RATES if RATES[c] * nch >= 8e6]
    sc.configure(1, nch, 1, 1)
    for code in codes:
        sc.out(0xE2, code)
        nominal = RATES[code] * nch
        results = []
        for seconds in (short, long_, short, long_):
            n = int(nominal * seconds) // 512 * 512
            buf = (ctypes.c_ubyte * n)()
            sc.flush()
            t = time.perf_counter()
            sc.start()
            got = len(sc.read(n, 10000, buf))
            dt = time.perf_counter() - t
            sc.stop()
            results.append((got, dt))
            time.sleep(0.05)
        rates = [(l[0] - s[0]) / (l[1] - s[1]) for s, l in ((results[0], results[1]), (results[2], results[3]))]
        print('code %2d, %d ch: steady %.2f / %.2f MB/s, nominal %.2f MB/s -> %.1f%%  (latency ~%.0f ms)' % (
            code, nch, rates[0] / 1e6, rates[1] / 1e6, nominal / 1e6, 100 * min(rates) / nominal,
            1000 * (results[0][1] - results[0][0] / rates[0])))


def test_dump(sc, a):
    nch = a.get('ch', 2)
    sc.configure(a.get('rate', 8), nch, a.get('gain', 1), a.get('gain2', a.get('gain', 1)),
                 cal=a.get('cal', 1000))
    sc.start()
    d = sc.read(a.get('bytes', 1 << 20))
    sc.stop()
    path = 'dump_r%d_%dch.bin' % (a.get('rate', 8), nch)
    open(path, 'wb').write(d)
    print('wrote', path, len(d))


if __name__ == '__main__':
    test = globals()['test_' + sys.argv[1]]
    sc = Scope()
    try:
        test(sc, args(sys.argv[2:]))
    finally:
        sc.close()
