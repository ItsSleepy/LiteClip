using System.IO.Pipes;
using System.Runtime.InteropServices;
using LiteClip.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace LiteClip.Media;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// One captured device (game sound via loopback, or a mic), converted to 48 kHz stereo float
/// and held in a short ring the pipe writer drains. The resample ratio is nudged a few ppm
/// at a time so the device clock can't drift away from the video clock.
/// </summary>
public sealed class AudioSource : IDisposable
{
    public const int Rate = 48000;
    public readonly string Kind; // "game" or "mic"
    readonly string _deviceId;
    public volatile float Gain = 1f;
    public string DeviceName { get; private set; } = "";

    WasapiCapture? _cap;
    WdlResampler? _rs;
    int _inRate, _inCh;
    bool _isFloat; int _bits;
    volatile bool _disposed;
    readonly object _lock = new();
    float[] _ring = new float[Rate * 2 * 4]; // 4 s stereo
    int _head, _count;                          // in floats
    float _peak;
    double _adjust;                             // resample ratio nudge (-0.002..0.002)
    double _appliedAdjust = double.NaN;
    System.Threading.Timer? _retry;

    public AudioSource(string kind, string deviceId) { Kind = kind; _deviceId = deviceId; }

    public int AvailableFrames { get { lock (_lock) return _count / 2; } }

    /// <summary>Peak level since the last call, 0..1 (after gain).</summary>
    public float TakePeak() { lock (_lock) { var p = _peak; _peak = 0; return p; } }

    public void SetDriftAdjust(double a) => Volatile.Write(ref _adjust, Math.Clamp(a, -0.002, 0.002));

    public void Start()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice dev = Kind == "game"
                ? (string.IsNullOrEmpty(_deviceId) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(_deviceId))
                : (string.IsNullOrEmpty(_deviceId) ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : en.GetDevice(_deviceId));
            DeviceName = dev.FriendlyName;
            _cap = Kind == "game" ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, true, 20);
            var f = _cap.WaveFormat;
            _inRate = f.SampleRate; _inCh = f.Channels; _bits = f.BitsPerSample;
            _isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            _rs = new WdlResampler();
            _rs.SetMode(true, 2, false);
            _rs.SetFilterParms();
            _rs.SetFeedMode(true);
            _appliedAdjust = double.NaN;
            _cap.DataAvailable += OnData;
            _cap.RecordingStopped += (_, e) =>
            {
                if (_disposed) return;
                Log.Warn($"{Kind} audio stopped{(e.Exception != null ? ": " + e.Exception.Message : "")}, retrying");
                ScheduleRetry();
            };
            _cap.StartRecording();
            Log.Info($"{Kind} audio: {DeviceName} ({_inRate} Hz, {_inCh} ch, {_bits}-bit{(_isFloat ? " float" : "")})");
        }
        catch (Exception ex)
        {
            Log.Warn($"{Kind} audio unavailable: {ex.Message}");
            ScheduleRetry();
        }
    }

    void ScheduleRetry()
    {
        try { _cap?.Dispose(); } catch { }
        _cap = null;
        _retry?.Dispose();
        _retry = new System.Threading.Timer(_ => { if (!_disposed) Start(); }, null, 3000, Timeout.Infinite);
    }

    /// <summary>Restart on the current Windows default device (called when the default changes).</summary>
    public void Reopen()
    {
        if (_disposed || !string.IsNullOrEmpty(_deviceId)) return;
        try { _cap?.StopRecording(); _cap?.Dispose(); } catch { }
        _cap = null;
        Start();
    }

    float[] _conv = new float[4096];
    float[] _outBuf = new float[8192];

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _rs == null || e.BytesRecorded == 0) return;
        int bytesPerSample = _bits / 8;
        int frames = e.BytesRecorded / (bytesPerSample * _inCh);
        if (_conv.Length < frames * 2) _conv = new float[frames * 2];
        ToStereo(e.Buffer, frames, _conv);

        var adj = Volatile.Read(ref _adjust);
        if (adj != _appliedAdjust)
        {
            _rs.SetRates(_inRate, Rate * (1 - adj));
            _appliedAdjust = adj;
        }
        int need = _rs.ResamplePrepare(frames, 2, out var inBuf, out var inOff);
        Array.Copy(_conv, 0, inBuf, inOff, need * 2);
        int maxOut = (int)(frames * (Rate / (double)_inRate) * 1.01) + 64;
        if (_outBuf.Length < maxOut * 2) _outBuf = new float[maxOut * 2];
        int got = _rs.ResampleOut(_outBuf, 0, need, maxOut, 2);
        Push(_outBuf, got * 2);
    }

    /// <summary>Any WASAPI format to interleaved stereo float, with a proper downmix for surround.</summary>
    void ToStereo(byte[] buf, int frames, float[] dst)
    {
        int ch = _inCh, bps = _bits / 8;
        float g = Gain;
        for (int i = 0; i < frames; i++)
        {
            int baseIdx = i * ch * bps;
            float S(int c)
            {
                int o = baseIdx + c * bps;
                if (_isFloat) return BitConverter.ToSingle(buf, o);
                return _bits switch
                {
                    16 => BitConverter.ToInt16(buf, o) / 32768f,
                    24 => ((buf[o] | (buf[o + 1] << 8) | ((sbyte)buf[o + 2] << 16))) / 8388608f,
                    32 => BitConverter.ToInt32(buf, o) / 2147483648f,
                    _ => 0f,
                };
            }
            float l, r;
            if (ch == 1) l = r = S(0);
            else if (ch >= 6)
            {   // FL FR FC LFE BL BR [SL SR]
                float c = S(2) * 0.707f;
                l = S(0) + c + S(4) * 0.707f + (ch >= 8 ? S(6) * 0.707f : 0);
                r = S(1) + c + S(5) * 0.707f + (ch >= 8 ? S(7) * 0.707f : 0);
                l *= 0.6f; r *= 0.6f;
            }
            else { l = S(0); r = S(1); }
            dst[i * 2] = l * g;
            dst[i * 2 + 1] = r * g;
        }
    }

    void Push(float[] src, int n)
    {
        lock (_lock)
        {
            float pk = _peak;
            for (int i = 0; i < n; i++) { var a = Math.Abs(src[i]); if (a > pk) pk = a; }
            _peak = pk;
            int cap = _ring.Length;
            for (int i = 0; i < n; i++)
            {
                int tail = (_head + _count) % cap;
                _ring[tail] = src[i];
                if (_count < cap) _count++;
                else _head = (_head + 1) % cap; // full: drop oldest
            }
        }
    }

    /// <summary>Copies up to <paramref name="frames"/> stereo frames out of the ring.</summary>
    public int Read(float[] dst, int offsetFloats, int frames)
    {
        lock (_lock)
        {
            int n = Math.Min(frames * 2, _count) & ~1;
            int cap = _ring.Length;
            for (int i = 0; i < n; i++) dst[offsetFloats + i] = _ring[(_head + i) % cap];
            _head = (_head + n) % cap;
            _count -= n;
            return n / 2;
        }
    }

    public void Discard(int frames)
    {
        lock (_lock)
        {
            int n = Math.Min(frames * 2, _count) & ~1;
            _head = (_head + n) % _ring.Length;
            _count -= n;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _retry?.Dispose();
        try { _cap?.StopRecording(); } catch { }
        try { _cap?.Dispose(); } catch { }
        _cap = null;
    }
}

/// <summary>
/// Feeds one audio source into ffmpeg through a named pipe on a fixed clock:
/// sample N always means "T0 + N/48000", whatever the device does. Gaps (loopback goes
/// quiet when nothing plays) become silence; the source's resample ratio is trimmed so its
/// buffer level stays put, which is what keeps long sessions in sync.
/// </summary>
public sealed class PipeFeed : IDisposable
{
    const int Rate = AudioSource.Rate;
    const int TargetFrames = Rate * 40 / 1000;    // ~40 ms cushion absorbs packet jitter
    const int HighFrames = Rate * 120 / 1000;     // beyond this something stalled: skip ahead
    public const int LatencyMs = 50;              // cushion + typical device latency

    public readonly string PipeName;
    readonly NamedPipeServerStream _pipe;
    readonly AudioSource? _src;
    readonly long _t0;
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;
    public long FramesWritten;
    public volatile bool Connected;

    public PipeFeed(string name, AudioSource? src, long t0Qpc100ns)
    {
        PipeName = name;
        _src = src;
        _t0 = t0Qpc100ns;
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
    }

    public string FfmpegPath => $@"\\.\pipe\{PipeName}";

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "pipe-" + PipeName, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Run()
    {
        try
        {
            _pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
            Connected = true;
            var buf = new float[Rate * 2]; // up to 1 s per write
            bool buffering = true;
            double level = TargetFrames;
            timeBeginPeriod(1);
            while (!_cts.IsCancellationRequested)
            {
                long due = (Native.Qpc100ns() - _t0) * Rate / 10_000_000 - Rate * LatencyMs / 1000;
                long need = due - FramesWritten;
                if (need <= 0) { Thread.Sleep(5); continue; }
                int n = (int)Math.Min(need, Rate);
                int avail = _src?.AvailableFrames ?? 0;

                if (avail - n > HighFrames) { _src!.Discard(avail - n - TargetFrames); avail = n + TargetFrames; }

                int got = 0;
                if (_src != null)
                {
                    if (buffering) { if (avail >= TargetFrames + n) buffering = false; }
                    if (!buffering)
                    {
                        got = _src.Read(buf, 0, n);
                        if (got < n) buffering = true; // ran dry (quiet loopback, or a hiccup)
                    }
                    level = level * 0.98 + _src.AvailableFrames * 0.02;
                    // too much queued -> device clock runs fast -> make fewer output samples
                    if (!buffering) _src.SetDriftAdjust((level - TargetFrames) / TargetFrames * 0.0005);
                }
                if (got < n) Array.Clear(buf, got * 2, (n - got) * 2);

                _pipe.Write(MemoryMarshal.AsBytes(buf.AsSpan(0, n * 2)));
                FramesWritten += n;
                Thread.Sleep(10);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* ffmpeg closed the pipe */ }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Error($"audio pipe {PipeName}", ex); }
        finally { timeEndPeriod(1); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pipe.Dispose(); } catch { }
        _thread?.Join(500);
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}

/// <summary>Owns the game and mic sources and follows Windows default-device changes.</summary>
public sealed class AudioEngine : IDisposable, IMMNotificationClient
{
    readonly object _gate = new();
    MMDeviceEnumerator? _en;
    public AudioSource? Game { get; private set; }
    public AudioSource? Mic { get; private set; }
    int _users;
    string _key = "";

    public AudioEngine()
    {
        try { _en = new MMDeviceEnumerator(); _en.RegisterEndpointNotificationCallback(this); } catch { }
    }

    /// <summary>Start (or reconfigure) capture. Each Acquire needs a matching Release.</summary>
    public void Acquire(Settings s)
    {
        lock (_gate)
        {
            _users++;
            Configure(s);
        }
    }

    public void Configure(Settings s)
    {
        lock (_gate)
        {
            if (_users == 0) return;
            var key = $"{s.RecordGameAudio}|{s.GameDevice}|{s.RecordMic}|{s.MicDevice}";
            if (key != _key)
            {
                _key = key;
                Game?.Dispose(); Mic?.Dispose();
                Game = s.RecordGameAudio ? new AudioSource("game", s.GameDevice) : null;
                Mic = s.RecordMic ? new AudioSource("mic", s.MicDevice) : null;
                Game?.Start(); Mic?.Start();
            }
            if (Game != null) Game.Gain = s.GameVolume / 100f;
            if (Mic != null) Mic.Gain = s.MicVolume / 100f;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_users > 0) _users--;
            if (_users == 0)
            {
                Game?.Dispose(); Mic?.Dispose();
                Game = Mic = null;
                _key = "";
            }
        }
    }

    public (float game, float mic) Levels() => (Game?.TakePeak() ?? 0, Mic?.TakePeak() ?? 0);

    public static List<AudioDevice> Devices(bool capture)
    {
        var list = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            try { def = en.GetDefaultAudioEndpoint(capture ? DataFlow.Capture : DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(capture ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == def));
        }
        catch (Exception ex) { Log.Warn("listing audio devices: " + ex.Message); }
        return list;
    }

    // IMMNotificationClient: follow the Windows default device when the user hasn't picked one
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (flow == DataFlow.Render && role == Role.Multimedia) Game?.Reopen();
                if (flow == DataFlow.Capture && role == Role.Multimedia) Mic?.Reopen();
            }
        });
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _en?.UnregisterEndpointNotificationCallback(this); _en?.Dispose(); } catch { }
        Game?.Dispose(); Mic?.Dispose();
    }
}
