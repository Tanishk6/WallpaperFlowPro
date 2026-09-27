using NAudio.CoreAudioApi;
using NAudio.Wave;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Audio;

/// <summary>
/// Captures system audio (WASAPI loopback) or a microphone, analyses it and emits band data at 30 Hz.
/// Capture only runs while an audio-reactive wallpaper is actually playing.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly Func<AudioSettings> _settings;
    private readonly object _gate = new();
    private IWaveIn? _capture;
    private SpectrumAnalyzer? _analyzer;
    private float[] _ringL = Array.Empty<float>(), _ringR = Array.Empty<float>();
    private int _ringPos;
    private Timer? _emitTimer;
    private DateTime _lastData;
    private float _level;
    private bool _wanted;
    private float[] _outL = new float[64], _outR = new float[64];

    /// <summary>(left bands, right bands, level). Arrays are reused; copy if you keep them.</summary>
    public event Action<float[], float[], float>? Spectrum;
    public bool IsRunning => _capture != null;

    public AudioService(Func<AudioSettings> settings) => _settings = settings;

    public void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            if (wanted == _wanted) return;
            _wanted = wanted;
            if (wanted) StartCore(); else StopCore();
        }
    }

    public void Restart()
    {
        lock (_gate)
        {
            StopCore();
            if (_wanted) StartCore();
        }
    }

    private void StartCore()
    {
        try
        {
            var cfg = _settings();
            IWaveIn capture;
            if (cfg.Source == AudioSource.Microphone)
            {
                var dev = !string.IsNullOrEmpty(cfg.DeviceId)
                    ? new MMDeviceEnumerator().GetDevice(cfg.DeviceId)
                    : WasapiCapture.GetDefaultCaptureDevice();
                capture = new WasapiCapture(dev);
            }
            else capture = new WasapiLoopbackCapture();

            var fmt = capture.WaveFormat;
            _analyzer = new SpectrumAnalyzer(fmt.SampleRate, cfg.Bands) { Smoothing = cfg.Smoothing, Gain = cfg.Gain };
            _ringL = new float[_analyzer.FftSize];
            _ringR = new float[_analyzer.FftSize];
            _outL = new float[cfg.Bands];
            _outR = new float[cfg.Bands];
            _ringPos = 0;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            _emitTimer = new Timer(_ => Emit(), null, 33, 33);
            Log.Info($"Audio capture started ({cfg.Source}, {fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample} bit)");
        }
        catch (Exception ex)
        {
            Log.Warn("Audio capture unavailable: " + ex.Message);
            _capture = null;
        }
    }

    private void StopCore()
    {
        _emitTimer?.Dispose();
        _emitTimer = null;
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try
        {
            c.DataAvailable -= OnData;
            c.RecordingStopped -= OnStopped;
            c.StopRecording();
        }
        catch { }
        c.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = (sender as IWaveIn)?.WaveFormat;
        if (fmt == null || e.BytesRecorded == 0) return;
        int ch = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * ch);
        bool isFloat = fmt.BitsPerSample == 32;
        for (int f = 0; f < frames; f++)
        {
            int o = f * ch * bytesPerSample;
            float l = Read(e.Buffer, o, isFloat, bytesPerSample);
            float r = ch > 1 ? Read(e.Buffer, o + bytesPerSample, isFloat, bytesPerSample) : l;
            _ringL[_ringPos] = l;
            _ringR[_ringPos] = r;
            _ringPos = (_ringPos + 1) % _ringL.Length;
        }
        _lastData = DateTime.UtcNow;
    }

    private static float Read(byte[] b, int o, bool isFloat, int size) =>
        isFloat ? BitConverter.ToSingle(b, o) : size == 2 ? BitConverter.ToInt16(b, o) / 32768f : 0f;

    private void Emit()
    {
        var a = _analyzer;
        if (a == null) return;
        if ((DateTime.UtcNow - _lastData).TotalMilliseconds > 120)
        {
            a.DecaySilence();
            _level *= 0.8f;
        }
        else
        {
            int n = _ringL.Length;
            Span<float> l = stackalloc float[n];
            Span<float> r = stackalloc float[n];
            for (int i = 0; i < n; i++)
            {
                int idx = (_ringPos + i) % n; // oldest → newest
                l[i] = _ringL[idx];
                r[i] = _ringR[idx];
            }
            _level = a.Process(l, r);
        }
        a.CopyTo(_outL, _outR);
        try { Spectrum?.Invoke(_outL, _outR, _level); } catch (Exception ex) { Log.Debug("Spectrum handler: " + ex.Message); }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        Log.Warn("Audio capture stopped: " + e.Exception.Message + " — retrying");
        _ = Task.Delay(2000).ContinueWith(_ => Restart());
    }

    public static IReadOnlyList<(string Id, string Name)> Microphones()
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => (d.ID, d.FriendlyName)).ToList();
        }
        catch { return Array.Empty<(string, string)>(); }
    }

    public void Dispose()
    {
        lock (_gate) { _wanted = false; StopCore(); }
    }
}
