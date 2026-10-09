using System.Runtime.InteropServices;
using MicOptimizer.Core.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicOptimizer.Audio;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public enum LatencyMode
{
    /// <summary>Windows low-latency shared mode. Works with any interface and leaves it available to other apps.</summary>
    Standard,
    /// <summary>The interface's ASIO driver for input (a few milliseconds faster). Falls back to Standard if unavailable.</summary>
    Lowest,
}

/// <summary>Where the delay between the microphone and the virtual cable comes from, in milliseconds.</summary>
public sealed record LatencyBreakdown(string InputPath, double InputMs, double ProcessingMs, double BufferMs, double OutputMs)
{
    public double TotalMs => InputMs + ProcessingMs + BufferMs + OutputMs;
}

/// <summary>
/// Captures one microphone channel, runs the processing chain and plays the result to a render device (VB-Cable).
/// Everything is tuned for delay: low-latency shared-mode streams on real-time threads, an optional ASIO input,
/// and a tiny drift-correcting buffer between the two devices' clocks.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private IDisposable? _input;            // WasapiRecorder or AsioDevice
    private WasapiPlayer? _player;
    private ElasticBuffer? _elastic;
    private ElasticSource? _source;
    private float[] _mono = new float[4096];
    private volatile float _inPeak, _outPeak;
    private double _inputMs, _outputMs;
    private string _inputPath = "";
    private int _rate;
    private Timer? _cushionTuner;
    private int _lastUnderruns;

    public ProcessingChain? Chain { get; private set; }
    public bool IsRunning => _input is not null;
    public LatencyMode ActiveMode { get; private set; }
    /// <summary>Why the requested latency mode could not be used (null when it was honoured).</summary>
    public string? ModeNote { get; private set; }

    /// <summary>Peak of the most recent audio block (kept for the command-line probe).</summary>
    public float InputPeak { get; private set; }
    public float OutputPeak { get; private set; }

    /// <summary>Raised on a background thread when audio stops because of an error (interface unplugged, driver reset...).</summary>
    public event Action<string>? Faulted;

    public static IReadOnlyList<AudioDevice> ListInputs() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDevice> ListOutputs() => List(DataFlow.Render);

    private static List<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
    }

    /// <summary>The current delay estimate. Processing time follows the chain, so it changes if noise removal is switched.</summary>
    public LatencyBreakdown? Latency
    {
        get
        {
            var chain = Chain; var elastic = _elastic;
            if (chain is null || elastic is null || _rate == 0) return null;
            return new LatencyBreakdown(_inputPath, _inputMs, chain.LatencyMs, elastic.CushionFrames * 1000.0 / _rate, _outputMs);
        }
    }

    /// <summary>
    /// Listens to every channel of an input device for a moment and returns each channel's peak (linear).
    /// Used to find which input of an interface the microphone is plugged into.
    /// </summary>
    public static async Task<float[]> MeasureChannelsAsync(string inputId, int durationMs)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var recorder = new WasapiRecorderBuilder().WithDevice(enumerator.GetDevice(inputId)).WithEventSync().Build();
        var fmt = recorder.WaveFormat;
        int channels = fmt.Channels, bytes = fmt.BitsPerSample / 8;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        var peaks = new float[channels];

        recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            int frameBytes = bytes * channels;
            for (int f = 0; f + frameBytes <= buffer.Length; f += frameBytes)
                for (int c = 0; c < channels; c++)
                    peaks[c] = MathF.Max(peaks[c], MathF.Abs(ReadSample(buffer, f + c * bytes, bytes, isFloat)));
        };
        recorder.StartRecording();
        await Task.Delay(durationMs);
        recorder.StopRecording();
        return peaks;
    }

    /// <summary>Returns the loudest input and output sample since the previous call, then resets (for smooth meters).</summary>
    public (float Input, float Output) TakePeaks()
    {
        var result = (_inPeak, _outPeak);
        _inPeak = 0; _outPeak = 0;
        return result;
    }

    /// <param name="inputChannel">0-based channel on the interface.</param>
    /// <param name="configure">Called with the new chain before audio starts, so settings are in place from the first sample.</param>
    public void Start(string inputId, string outputId, int inputChannel = 0, LatencyMode mode = LatencyMode.Standard,
                      Action<ProcessingChain>? configure = null)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        var inDev = enumerator.GetDevice(inputId);
        var chain = SetupOutput(enumerator.GetDevice(outputId), configure);
        var player = _player!;

        try
        {
            ModeNote = null;
            bool started = false;
            if (mode == LatencyMode.Lowest)
            {
                started = TryStartAsio(inDev.FriendlyName, inputChannel, chain, out var note);
                if (!started) ModeNote = note;
            }
            if (!started) StartWasapiInput(inDev, inputChannel, chain);

            player.Play();
        }
        catch
        {
            Stop();
            throw;
        }

        _lastUnderruns = 0;
        _cushionTuner = new Timer(_ => TuneCushion(), null, 1000, 1000);
    }

    /// <summary>Underruns since start (audible glitches), for the probe's measurements.</summary>
    public int Underruns => _elastic?.Underruns ?? 0;

    /// <summary>Per-interval buffer statistics for diagnosing clock drift and timing problems.</summary>
    public (long Written, long Read, int MinFill, int MaxFill, int Writes)? TakeBufferStats() => _elastic?.TakeStats();

    /// <summary>Opens the render device and creates the buffer and chain. Its format and period decide the rate we run at.</summary>
    private ProcessingChain SetupOutput(MMDevice outDev, Action<ProcessingChain>? configure)
    {
        var player = new WasapiPlayerBuilder()
            .WithDevice(outDev).WithEventSync().WithLowLatency(true).WithMmcssThreadPriority("Pro Audio").Build();
        var outFormat = player.DeviceMixFormat;
        _rate = outFormat.SampleRate;
        var source = new ElasticSource(outFormat);
        player.Init(source);
        _outputMs = player.LatencyMilliseconds;

        // The render callback pulls about 2.7 ms at a time and the virtual cable's clock is uneven, so start with two
        // such chunks (about 6 ms) in hand; the tuner raises it if the player ever still runs dry.
        int cushion = Math.Max(_rate / 1000 * 6, (int)Math.Ceiling(2.0 * (_outputMs + 0.7) * _rate / 1000.0));
        var elastic = new ElasticBuffer(cushion);
        source.Attach(elastic);

        var chain = new ProcessingChain(_rate);
        configure?.Invoke(chain);

        _player = player; _source = source; _elastic = elastic; Chain = chain;
        return chain;
    }

    /// <summary>For measurements only: runs just the output side. Audio is supplied through <see cref="Feed"/>.</summary>
    public void StartOutputOnly(string outputId, Action<ProcessingChain>? configure = null)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        SetupOutput(enumerator.GetDevice(outputId), configure);
        _inputPath = "synthetic"; _inputMs = 0;
        _input = new NoInput();
        _player!.Play();
    }

    /// <summary>For measurements only: pushes a block through the chain and into the output as if it had just been captured.</summary>
    public void Feed(ReadOnlySpan<float> block)
    {
        var chain = Chain; var elastic = _elastic;
        if (chain is null || elastic is null) return;
        if (_mono.Length < block.Length) _mono = new float[block.Length];
        block.CopyTo(_mono);
        Process(chain, elastic, _mono.AsSpan(0, block.Length));
    }

    private sealed class NoInput : IDisposable { public void Dispose() { } }

    private void StartWasapiInput(MMDevice inDev, int inputChannel, ProcessingChain chain)
    {
        var recorder = new WasapiRecorderBuilder()
            .WithDevice(inDev).WithEventSync().WithLowLatency(true).WithMmcssThreadPriority("Pro Audio").Build();
        var fmt = recorder.WaveFormat;
        if (fmt.SampleRate != _rate)
            throw new InvalidOperationException(
                $"The microphone runs at {fmt.SampleRate} Hz but the output runs at {_rate} Hz. Set both to the same rate in Windows Sound settings.");

        int channels = fmt.Channels, bytes = fmt.BitsPerSample / 8;
        int channel = Math.Clamp(inputChannel, 0, channels - 1);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        var elastic = _elastic!;

        recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            int frameBytes = bytes * channels;
            int frames = buffer.Length / frameBytes;
            if (_mono.Length < frames) _mono = new float[frames];
            for (int i = 0; i < frames; i++)
                _mono[i] = ReadSample(buffer, i * frameBytes + channel * bytes, bytes, isFloat);
            Process(chain, elastic, _mono.AsSpan(0, frames));
        };
        recorder.RecordingStopped += (_, e) => { if (e.Exception is not null) Faulted?.Invoke(e.Exception.Message); };

        ActiveMode = LatencyMode.Standard;
        _input = recorder;
        recorder.StartRecording();
        _inputPath = recorder.LowLatencyActive ? "Windows low-latency" : "Windows shared";
        _inputMs = recorder.LatencyMilliseconds;
    }

    /// <summary>Opens the interface's ASIO driver for input. Returns false (with a reason) instead of throwing.</summary>
    private bool TryStartAsio(string deviceName, int inputChannel, ProcessingChain chain, out string note)
    {
        note = "";
        AsioDevice? asio = null;
        try
        {
            var candidates = AsioDevice.GetDriverNames()
                .Where(d => SharesNameWith(deviceName, d)).ToList();
            if (candidates.Count == 0) { note = "No ASIO driver found for this interface."; return false; }

            var openErrors = new List<string>();
            foreach (var driverName in candidates)
            {
                try { asio = AsioDevice.Open(driverName); break; }
                catch (Exception ex) { openErrors.Add($"{driverName}: {ex.Message}"); }   // try the next candidate, e.g. a Thunderbolt driver on a USB machine
            }
            if (asio is null) { note = "The ASIO driver could not be opened (" + string.Join("; ", openErrors) + ")."; return false; }

            if (!asio.IsSampleRateSupported(_rate))
            {
                note = $"The ASIO driver does not support {_rate} Hz.";
                asio.Dispose(); return false;
            }

            var elastic = _elastic!;
            asio.AudioCaptured += (_, e) =>
            {
                var samples = e.GetChannel(0);
                if (_mono.Length < samples.Length) _mono = new float[samples.Length];
                samples.CopyTo(_mono);
                Process(chain, elastic, _mono.AsSpan(0, samples.Length));
            };
            asio.DriverResetRequest += (_, _) => Faulted?.Invoke("The ASIO driver was reset (for example the buffer size was changed)");
            asio.Stopped += (_, _) => { };

            asio.InitRecording(new AsioRecordingOptions
            {
                InputChannels = new[] { inputChannel },
                SampleRate = _rate,
                BufferSize = 64,
            });

            _inputMs = (asio.InputLatencySamples) * 1000.0 / _rate;
            _inputPath = $"ASIO ({asio.DriverName}, {asio.FramesPerBuffer} samples)";
            ActiveMode = LatencyMode.Lowest;
            _input = asio;
            asio.Start();
            return true;
        }
        catch (Exception ex)
        {
            note = "ASIO input failed: " + ex.Message;

            try { asio?.Dispose(); } catch { /* already unusable */ }
            if (ReferenceEquals(_input, asio)) _input = null;
            return false;
        }
    }

    private static bool SharesNameWith(string deviceName, string driverName)
    {
        string[] generic = { "asio", "usb", "audio", "driver", "thunderbolt", "pci", "the", "and" };
        foreach (var token in driverName.Split(new[] { ' ', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 3 || generic.Contains(token.ToLowerInvariant())) continue;
            if (deviceName.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void Process(ProcessingChain chain, ElasticBuffer elastic, Span<float> mono)
    {
        if (mono.IsEmpty) return;   // WASAPI also signals empty packets
        float inPeak = Peak(mono);
        chain.Process(mono);
        float outPeak = Peak(mono);

        InputPeak = inPeak; OutputPeak = outPeak;
        if (inPeak > _inPeak) _inPeak = inPeak;
        if (outPeak > _outPeak) _outPeak = outPeak;

        elastic.Write(mono);
    }

    /// <summary>If the player ever ran dry, make the cushion a little bigger: lowest delay that is still glitch-free.</summary>
    private void TuneCushion()
    {
        var elastic = _elastic;
        if (elastic is null) return;
        int underruns = elastic.Underruns;
        if (underruns > _lastUnderruns)
        {
            int max = _rate / 50;                                  // never more than 20 ms
            elastic.CushionFrames = Math.Min(max, elastic.CushionFrames + _rate / 1000 * 1);
            _lastUnderruns = underruns;
        }
    }

    public void Stop()
    {
        _cushionTuner?.Dispose(); _cushionTuner = null;
        var input = _input; var player = _player; var chain = Chain;
        _input = null; _player = null; _source = null; _elastic = null; Chain = null;

        try
        {
            switch (input)
            {
                case WasapiRecorder r: r.StopRecording(); break;
                case AsioDevice a: a.Stop(); break;
            }
        }
        catch (Exception) { /* the device may already be gone */ }
        input?.Dispose();

        try { player?.Stop(); } catch (Exception) { /* the device may already be gone */ }
        player?.Dispose();
        chain?.Dispose();   // RnnoiseStage ignores audio after disposal, so a late callback is harmless
        _inPeak = 0; _outPeak = 0;
    }

    public void Dispose() => Stop();

    private static float Peak(ReadOnlySpan<float> s)
    {
        float p = 0;
        foreach (var v in s) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    private static float ReadSample(ReadOnlySpan<byte> b, int o, int bytes, bool isFloat)
    {
        if (isFloat) return BitConverter.ToSingle(b.Slice(o, 4));
        return bytes switch
        {
            2 => BitConverter.ToInt16(b.Slice(o, 2)) / 32768f,
            3 => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(b.Slice(o, 4)) / 2147483648f,
            _ => 0f,
        };
    }

    /// <summary>Feeds the player from the elastic buffer: mono audio copied to the first two output channels.</summary>
    private sealed class ElasticSource : IWaveProvider
    {
        private readonly int _channels;
        private ElasticBuffer? _buffer;
        private float[] _scratch = new float[1024];

        public ElasticSource(WaveFormat format)
        {
            WaveFormat = format;
            _channels = format.Channels;
        }

        public WaveFormat WaveFormat { get; }
        public void Attach(ElasticBuffer buffer) => _buffer = buffer;

        public int Read(Span<byte> destination)
        {
            int frames = destination.Length / (4 * _channels);
            if (_scratch.Length < frames) _scratch = new float[frames];
            var mono = _scratch.AsSpan(0, frames);

            if (_buffer is { } buffer) buffer.Read(mono); else mono.Clear();

            var output = MemoryMarshal.Cast<byte, float>(destination);
            for (int i = 0; i < frames; i++)
            {
                int o = i * _channels;
                output[o] = mono[i];
                if (_channels > 1) output[o + 1] = mono[i];
                for (int c = 2; c < _channels; c++) output[o + c] = 0f;
            }
            return destination.Length;
        }
    }
}
