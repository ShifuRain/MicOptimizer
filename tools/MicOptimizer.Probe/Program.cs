using MicOptimizer.Audio;
using MicOptimizer.Core.Dsp;

// Usage:
//   Probe                 list devices
//   Probe run [seconds]   Scarlett -> chain -> CABLE Input, with a text level meter (default 20 s)
//   Probe run 20 bypass   same, with the chain bypassed (for A/B comparison)

var inputs = AudioEngine.ListInputs();
var outputs = AudioEngine.ListOutputs();

if (args.Length > 0 && args[0] == "latinfo")
{
    // What latency can each device really give us? Standard shared mode vs Windows' low-latency shared mode (IAudioClient3).
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var micDev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
    var cabDev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));

    Console.WriteLine($"Windows version: {Environment.OSVersion.Version}");
    foreach (bool low in new[] { false, true })
    {
        Console.WriteLine($"\n--- {(low ? "LOW-LATENCY shared mode (IAudioClient3)" : "standard shared mode")} ---");
        try
        {
            using var rec = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(micDev).WithEventSync().WithLowLatency(low).Build();
            rec.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { };
            rec.StartRecording();
            Thread.Sleep(500);
            Console.WriteLine($"capture {micDev.FriendlyName}: format {rec.WaveFormat}");
            Console.WriteLine($"   latency {rec.LatencyMilliseconds} ms, lowLatencyActive={rec.LowLatencyActive}, reason='{rec.LowLatencyUnavailableReason}'");
            rec.StopRecording();
        }
        catch (Exception ex) { Console.WriteLine($"capture failed: {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            using var player = new NAudio.Wave.WasapiPlayerBuilder().WithDevice(cabDev).WithEventSync().WithLowLatency(low).Build();
            var fmt = player.DeviceMixFormat;
            player.Init(new NAudio.Wave.SilenceProvider(fmt));
            Console.WriteLine($"render  {cabDev.FriendlyName}: mix format {fmt}");
            Console.WriteLine($"   latency {player.LatencyMilliseconds} ms, lowLatencyActive={player.LowLatencyActive}, reason='{player.LowLatencyUnavailableReason}'");
        }
        catch (Exception ex) { Console.WriteLine($"render failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    Console.WriteLine("\n--- EXCLUSIVE mode capture on the Scarlett (other apps cannot use the input while open) ---");
    foreach (int ms in new[] { 2, 3, 5, 10 })
    {
        foreach (var fmt in new NAudio.Wave.WaveFormat?[] { null, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new NAudio.Wave.WaveFormat(48000, 24, 2), new NAudio.Wave.WaveFormat(48000, 16, 2) })
        {
            try
            {
                var b = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(micDev).WithExclusiveMode().WithEventSync().WithBufferLength(ms);
                if (fmt is not null) b = b.WithFormat(fmt);
                using var rec = b.Build();
                int packets = 0;
                rec.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { packets++; };
                rec.StartRecording();
                Thread.Sleep(600);
                Console.WriteLine($"  request {ms,2} ms, format {(fmt?.ToString() ?? "(default)"),-48} -> latency {rec.LatencyMilliseconds} ms, packets in 0.6 s: {packets}  [{rec.WaveFormat}]");
                rec.StopRecording();
                break;   // first format that works for this size is enough
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  request {ms,2} ms, format {(fmt?.ToString() ?? "(default)"),-48} -> {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
    }
    return;
}

if (args.Length > 0 && args[0] == "pathcheck")
{
    // Adds and removes a dummy folder on the REAL user PATH (including the change broadcast), then checks it is restored exactly.
    string RawPath() => (string)Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment")!
        .GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)!;
    string before = RawPath();
    var userPath = new MicOptimizer.Core.PathSetup.UserPath();
    const string dummy = @"C:\MicOptimizer-pathcheck-dummy";
    try
    {
        Console.WriteLine($"added: {userPath.Add(dummy)}  contains: {userPath.Contains(dummy)}  added again (should be False): {userPath.Add(dummy)}");
        Console.WriteLine($"present in the registry value: {RawPath().Contains(dummy)}");
    }
    finally
    {
        Console.WriteLine($"removed: {userPath.Remove(dummy)}  contains afterwards: {userPath.Contains(dummy)}");
        Console.WriteLine($"PATH restored exactly: {RawPath() == before}");
    }
    return;
}

if (args.Length > 0 && args[0] == "mutetest")
{
    // Does AudioEngine.OutputMuted really silence what arrives on CABLE Output? (This is what keeps calibration off a call.)
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var cabIn = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));
    var cabOut = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));

    float peak = 0;
    using var detector = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(cabOut).WithEventSync().WithLowLatency(true).Build();
    int ch = detector.WaveFormat.Channels;
    detector.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
    {
        for (int i = 0; i + 4 <= buffer.Length; i += 4 * ch) peak = MathF.Max(peak, MathF.Abs(BitConverter.ToSingle(buffer.Slice(i, 4))));
    };
    detector.StartRecording();

    using var eng = new AudioEngine();
    eng.StartOutputOnly(cabIn.ID, c => c.Bypass = true);
    var block = new float[480];
    long n = 0;
    Timer1ms.Enable();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    foreach (var (label, muted) in new[] { ("live", false), ("MUTED", true), ("live again", false) })
    {
        eng.OutputMuted = muted;
        peak = 0;
        long until = n + 300;                                         // 3 seconds of 10 ms blocks
        double settleUntil = 0;
        while (n < until)
        {
            while (clock.Elapsed.TotalMilliseconds < n * 10) Thread.Sleep(1);
            for (int i = 0; i < block.Length; i++) block[i] = 0.1f * MathF.Sin(2 * MathF.PI * 1000f * (n * 480 + i) / 48000f);   // -20 dBFS tone
            eng.Feed(block);
            n++;
            if (n == until - 250) peak = 0;                           // ignore the first 0.5 s after switching (buffered audio drains)
        }
        Console.WriteLine($"  {label,-10}: peak on CABLE Output = {Db.ToDb(peak),7:0.0} dBFS   (tone sent at -20 dBFS)");
    }
    detector.StopRecording();
    return;
}

if (args.Length > 0 && args[0] == "autocal")
{
    // Runs the in-app calibration algorithm on the recordings made by `calibrate`.
    var rec = new MicOptimizer.Core.Calibration.CalibrationRecording(48000);
    foreach (var phase in Enum.GetValues<MicOptimizer.Core.Calibration.CalibrationPhase>())
    {
        string path = $"calibration/{phase.ToString().ToLowerInvariant()}.wav";
        if (!File.Exists(path)) continue;
        if (args.Contains("skip-" + phase.ToString().ToLowerInvariant())) continue;
        using var r = new NAudio.Wave.WaveFileReader(path);
        var l = new List<float>(); float[] f;
        while ((f = r.ReadNextSampleFrame()) is { Length: > 0 }) l.Add(f[0]);
        rec.Phases[phase] = l.ToArray();
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var analysis = MicOptimizer.Core.Calibration.GateCalibrator.Analyze(rec, new MicOptimizer.Core.Profiles.Profile());
    Console.WriteLine($"analysis took {sw.Elapsed.TotalSeconds:0.0} s for {analysis.Candidates.Count} candidates (phases: {string.Join(", ", rec.Phases.Keys)})");
    if (analysis.Error is not null) { Console.WriteLine("ERROR: " + analysis.Error); return; }
    foreach (var bias in Enum.GetValues<MicOptimizer.Core.Calibration.CalibrationBias>())
    {
        var res = analysis.Choose(bias);
        var c = res.Chosen;
        Console.WriteLine($"\n[{bias}] open {res.GateOpenDb:0} dB, close {res.GateCloseDb:0} dB, hold {res.GateHoldMs:0} ms   | silence open {c.SilenceOpenPercent:0}%, typing open {c.TypingOpenPercent:0}%, whisper kept {c.WhisperKeptPercent:0}%, speech kept {c.SpeechKeptPercent:0}%, shout kept {c.ShoutKeptPercent:0}%, gap {c.GapDb:0.0} dB");
        if (bias == MicOptimizer.Core.Calibration.CalibrationBias.Balanced)
        {
            foreach (var line in res.Summary) Console.WriteLine("   - " + line);
            foreach (var a in res.Advice) Console.WriteLine($"   [{a.Severity}] {a.Text}");
        }
    }
    return;
}

if (args.Length > 0 && args[0] == "latency")
{
    Timer1ms.Enable();
    // Click-to-click delay from "block handed to the pipeline" to "click seen on CABLE Output", for the old and new designs.
    // The chain is bypassed so this isolates buffering and devices; processing delay (RNNoise 10 ms, limiter 1 ms) is added separately.
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var cabIn = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));
    var cabOut = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));

    double Median(List<double> v) { v.Sort(); return v.Count == 0 ? double.NaN : v[v.Count / 2]; }

    List<double> Measure(Action<float[]> feed, int seconds)
    {
        var injected = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var results = new List<double>();
        long ignoreUntil = 0;
        using var detector = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(cabOut).WithEventSync().WithLowLatency(true)
            .WithMmcssThreadPriority("Pro Audio").Build();
        int ch = detector.WaveFormat.Channels;
        detector.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            int frames = buffer.Length / (4 * ch);
            for (int i = 0; i < frames; i++)
            {
                float v = BitConverter.ToSingle(buffer.Slice(i * 4 * ch, 4));
                if (MathF.Abs(v) < 0.15f) continue;
                long t = qpcPosition + (long)(i * 1e7 / 48000);                   // 100 ns units
                if (t < ignoreUntil) break;
                ignoreUntil = t + 3_000_000;                                    // ignore the rest of this click (300 ms)
                if (injected.TryDequeue(out long t0)) lock (results) results.Add((t - t0) / 10000.0);
                break;
            }
        };
        detector.StartRecording();

        var block = new float[480];
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double freq = System.Diagnostics.Stopwatch.Frequency;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        for (int n = 0; n < seconds * 100; n++)
        {
            long due = start + (long)(n * 0.010 * freq);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < due)
            {
                if ((due - System.Diagnostics.Stopwatch.GetTimestamp()) / freq > 0.002) Thread.Sleep(1); else Thread.SpinWait(50);
            }
            Array.Clear(block);
            bool click = n >= 200 && n % 60 == 0;                                // after 2 s of warm-up, every 600 ms
            if (click)
            {
                for (int i = 0; i < 24; i++) block[i] = 0.8f * MathF.Sin(2 * MathF.PI * 1000f * i / 48000f + 1.0f) ;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                injected.Enqueue((long)(now * 1e7 / freq));
            }
            feed(block);
        }
        Thread.Sleep(300);
        detector.StopRecording();
        lock (results) return new List<double>(results);
    }

    void Report(string name, List<double> r, string extra = "")
        => Console.WriteLine($"  {name,-34} median {Median(new List<double>(r)),5:0.0} ms   (n={r.Count}, min {(r.Count > 0 ? r.Min() : double.NaN):0.0}, max {(r.Count > 0 ? r.Max() : double.NaN):0.0}) {extra}");

    // FLOOR: a player whose callback emits a click straight into CABLE Input, nothing of ours in between.
    {
        long ignoreUntil = 0; var dets = new List<double>(); long lastClickAt = 0;
        using var det = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(cabOut).WithEventSync().WithLowLatency(true).WithMmcssThreadPriority("Pro Audio").Build();
        int dch = det.WaveFormat.Channels;
        det.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            int frames = buffer.Length / (4 * dch);
            for (int i = 0; i < frames; i++)
            {
                if (MathF.Abs(BitConverter.ToSingle(buffer.Slice(i * 4 * dch, 4))) < 0.15f) continue;
                long t = qpcPosition + (long)(i * 1e7 / 48000);
                if (t < ignoreUntil) break;
                ignoreUntil = t + 3_000_000;
                long t0 = Interlocked.Read(ref lastClickAt);
                if (t0 != 0) lock (dets) dets.Add((t - t0) / 10000.0);
                break;
            }
        };
        using var pl = new NAudio.Wave.WasapiPlayerBuilder().WithDevice(cabIn).WithEventSync().WithLowLatency(true).WithMmcssThreadPriority("Pro Audio").Build();
        var src = new ClickSource(pl.DeviceMixFormat, () => Interlocked.Exchange(ref lastClickAt, (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1e7 / System.Diagnostics.Stopwatch.Frequency)));
        pl.Init(src);
        det.StartRecording(); pl.Play();
        Thread.Sleep(9000);
        pl.Stop(); det.StopRecording();
        List<double> d; lock (dets) d = new List<double>(dets);
        Console.WriteLine($"FLOOR: click written straight into CABLE Input -> seen on CABLE Output: median {Median(new List<double>(d)):0.0} ms (n={d.Count}, min {(d.Count > 0 ? d.Min() : double.NaN):0.0}, max {(d.Count > 0 ? d.Max() : double.NaN):0.0})  [player period {pl.LatencyMilliseconds} ms]");
        Console.WriteLine();
    }

    Console.WriteLine("Delay from 'block handed to the pipeline' to 'click visible on CABLE Output' (chain bypassed):");

    // OLD design: 250 ms BufferedWaveProvider feeding the legacy WasapiOut with a 20 ms buffer.
    {
        var buf = new NAudio.Wave.BufferedWaveProvider(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), TimeSpan.FromMilliseconds(250)) { DiscardOnBufferOverflow = true };
        using var old = new NAudio.Wave.WasapiOut(cabIn, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 20);
        old.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(buf))));
        old.Play();
        var r = Measure(b => buf.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(b)), 10);
        Report("OLD  (BufferedWaveProvider+WasapiOut)", r);
        old.Stop();
    }
    Thread.Sleep(500);

    // NEW design: the real engine's output side.
    {
        using var eng = new AudioEngine();
        eng.StartOutputOnly(cabIn.ID, c => c.Bypass = true);
        var lat = eng.Latency!;
        var pollStop = false; var poll = Task.Run(() => { int sec = 0; while (!pollStop) { Thread.Sleep(1000); sec++; Console.WriteLine($"       t={sec,2}s underruns so far {eng.Underruns}, buffer cushion {eng.Latency?.BufferMs:0.0} ms"); } });
        var r = Measure(b => eng.Feed(b), 10);
        pollStop = true; Report("NEW  (elastic buffer + low-latency)", r, $"underruns {eng.Underruns}");
        Console.WriteLine($"       engine's own estimate for the output side: buffer {lat.BufferMs:0.0} ms + player {lat.OutputMs:0.0} ms = {lat.BufferMs + lat.OutputMs:0.0} ms");
    }
    return;
}

if (args.Length > 0 && args[0] == "coexist")
{
    // Run this while another process holds the Scarlett through ASIO: can ordinary Windows audio still use it?
    Thread.Sleep(3000);
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var capDev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active).First(d => d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
    var renDev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).First(d => d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
    try
    {
        using var rec = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(capDev).WithEventSync().Build();
        float peak = 0; int packets = 0;
        rec.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { packets++; for (int i = 0; i + 4 <= buffer.Length; i += 4) peak = MathF.Max(peak, MathF.Abs(BitConverter.ToSingle(buffer.Slice(i, 4)))); };
        rec.StartRecording(); Thread.Sleep(1500); rec.StopRecording();
        Console.WriteLine($"Windows capture from {capDev.FriendlyName} while ASIO is open: OK, {packets} packets, peak {Db.ToDb(peak):0.0} dBFS");
    }
    catch (Exception ex) { Console.WriteLine($"Windows capture while ASIO is open: FAILED {ex.GetType().Name}: {ex.Message}"); }
    try
    {
        using var play = new NAudio.Wave.WasapiPlayerBuilder().WithDevice(renDev).WithEventSync().Build();
        int reads = 0;
        play.Init(new CountingSource(play.DeviceMixFormat, _ => reads++));
        play.Play(); Thread.Sleep(1500); play.Stop();
        Console.WriteLine($"Windows playback to {renDev.FriendlyName} while ASIO is open: OK, {reads} callbacks");
    }
    catch (Exception ex) { Console.WriteLine($"Windows playback while ASIO is open: FAILED {ex.GetType().Name}: {ex.Message}"); }
    return;
}

if (args.Length > 0 && args[0] == "jitter")
{
    // How regularly do the capture and render callbacks actually fire, running together like in the engine?
    Timer1ms.Enable();
    bool lowLatency = !args.Contains("standard");
    bool mmcss = !args.Contains("nommcss");
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var micDev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
    var cabDev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));

    var capDeltas = new List<double>(); var capFrames = new List<int>(); var renDeltas = new List<double>(); var renFrames = new List<int>();
    long lastCap = 0, lastRen = 0; double freq = System.Diagnostics.Stopwatch.Frequency;

    var rb = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(micDev).WithEventSync().WithLowLatency(lowLatency);
    var pb = new NAudio.Wave.WasapiPlayerBuilder().WithDevice(cabDev).WithEventSync().WithLowLatency(lowLatency);
    if (mmcss) { rb = rb.WithMmcssThreadPriority("Pro Audio"); pb = pb.WithMmcssThreadPriority("Pro Audio"); }
    using var rec = rb.Build();
    using var play = pb.Build();
    int cch = rec.WaveFormat.Channels;
    rec.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastCap != 0) lock (capDeltas) { capDeltas.Add((now - lastCap) * 1000.0 / freq); capFrames.Add(buffer.Length / (4 * cch)); }
        lastCap = now;
    };
    var src = new CountingSource(play.DeviceMixFormat, frames =>
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastRen != 0) lock (renDeltas) { renDeltas.Add((now - lastRen) * 1000.0 / freq); renFrames.Add(frames); }
        lastRen = now;
    });
    play.Init(src);
    rec.StartRecording(); play.Play();
    Thread.Sleep(10000);
    play.Stop(); rec.StopRecording();

    string Describe(string name, List<double> d, List<int> f, double nominalMs)
    {
        List<double> s; List<int> ff; lock (d) { s = new List<double>(d); ff = new List<int>(f); }
        s.Sort();
        double P(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        int late = s.Count(x => x > nominalMs * 2 + 1.5);
        return $"{name}: {s.Count} callbacks, interval median {P(.5):0.00} ms, p99 {P(.99):0.00} ms, max {s[^1]:0.0} ms, gaps over {nominalMs * 2 + 1.5:0.0} ms: {late};  frames/callback: median {ff.OrderBy(x => x).ElementAt(ff.Count / 2)}, min {ff.Min()}, max {ff.Max()}";
    }
    Console.WriteLine($"[low-latency={lowLatency}, MMCSS={mmcss}]  capture period {rec.LatencyMilliseconds} ms, render period {play.LatencyMilliseconds} ms");
    Console.WriteLine("  " + Describe("capture", capDeltas, capFrames, rec.LatencyMilliseconds));
    Console.WriteLine("  " + Describe("render ", renDeltas, renFrames, play.LatencyMilliseconds));
    return;
}

if (args.Length > 0 && args[0] == "asioinfo")
{
    var names = NAudio.Wave.Asio.AsioDriver.GetAsioDriverNames();
    Console.WriteLine($"ASIO drivers installed: {(names.Length == 0 ? "none" : string.Join(" | ", names))}");
    foreach (var name in names)
    {
        // ASIO drivers are COM objects that expect a single-threaded apartment.
        var t = new Thread(() =>
        {
            NAudio.Wave.Asio.AsioDriver? drv = null;
            try
            {
                drv = NAudio.Wave.Asio.AsioDriver.GetAsioDriverByName(name);
                drv.Init(IntPtr.Zero);
                drv.GetChannels(out int ins, out int outs);
                drv.GetBufferSize(out int min, out int max, out int preferred, out int granularity);
                double rate = drv.GetSampleRate();
                drv.GetLatencies(out int inLat, out int outLat);
                Console.WriteLine($"\n{name}: v{drv.GetDriverVersion()}  {ins} in / {outs} out, sample rate {rate} Hz");
                Console.WriteLine($"   buffer size: min {min}, preferred {preferred}, max {max}, granularity {granularity} samples");
                Console.WriteLine($"   at {rate} Hz: min {min * 1000.0 / rate:0.0} ms, preferred {preferred * 1000.0 / rate:0.0} ms");
                Console.WriteLine($"   driver-reported latencies: input {inLat} samples ({inLat * 1000.0 / rate:0.0} ms), output {outLat} samples ({outLat * 1000.0 / rate:0.0} ms)");
                for (int c = 0; c < ins; c++) { var ci = drv.GetChannelInfo(c, true); Console.WriteLine($"   input {c}: '{ci.name}' type {ci.type}"); }
            }
            catch (Exception ex) { Console.WriteLine($"{name}: {ex.GetType().Name}: {ex.Message}"); }
            finally { try { drv?.ReleaseComAsioDriver(); } catch { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
    }
    return;
}

if (args.Length > 0 && args[0] == "dyn")
{
    // Compares leveler/compressor variants on the calibration recordings (level while speaking = p75 per phase).
    var names = new[] { "silence", "typing", "whisper", "speech", "shout" };
    var audio = names.ToDictionary(n => n, n =>
    {
        using var r = new NAudio.Wave.WaveFileReader($"calibration/{n}.wav");
        var l = new List<float>(); float[] f;
        while ((f = r.ReadNextSampleFrame()) is { Length: > 0 }) l.Add(f[0]);
        return l.ToArray();
    });
    const int Sr = 48000, Win = Sr / 50;
    float P75(float[] s)
    {
        var l = new List<float>();
        for (int k = 0; k + Win <= s.Length; k += Win)
        {
            double a = 0; for (int j = 0; j < Win; j++) a += s[k + j] * s[k + j];
            l.Add(Db.ToDb((float)Math.Sqrt(a / Win)));
        }
        l.Sort(); return l[(int)(.75 * l.Count)];
    }

    (string Name, float Boost, float Cut, float Target, float Ratio, float Thr, float Makeup)[] variants =
    {
        ("current (flat)",       15f, -12f, -20f, 3.0f, -20f, 3f),
        ("A: less lifting",       8f,  -4f, -22f, 2.5f, -18f, 3f),
        ("B: gentle",             6f,  -3f, -24f, 2.0f, -16f, 2f),
        ("C: natural",            4f,  -2f, -26f, 2.0f, -14f, 2f),
        ("D: almost raw",         3f,  -1f, -28f, 1.6f, -12f, 1f),
    };

    Console.WriteLine($"{"variant",-16} | in: whisper {P75(audio["whisper"]),6:0.0}  speech {P75(audio["speech"]),6:0.0}  shout {P75(audio["shout"]),6:0.0}  (spread {P75(audio["shout"]) - P75(audio["whisper"]):0.0} dB)");
    foreach (var v in variants)
    {
        var chain = new ProcessingChain(Sr);
        chain.Leveler.MaxBoostDb = v.Boost; chain.Leveler.MaxCutDb = v.Cut; chain.Leveler.TargetDb = v.Target;
        chain.Compressor.Ratio = v.Ratio; chain.Compressor.ThresholdDb = v.Thr; chain.Compressor.MakeupDb = v.Makeup;
        var o = new Dictionary<string, float>();
        foreach (var n in names)
        {
            var b = (float[])audio[n].Clone();
            for (int i = 0; i + 480 <= b.Length; i += 480) chain.Process(b.AsSpan(i, 480));
            o[n] = P75(b);
        }
        Console.WriteLine($"{v.Name,-16} | out: whisper {o["whisper"],6:0.0}  speech {o["speech"],6:0.0}  shout {o["shout"],6:0.0}  (spread {o["shout"] - o["whisper"]:0.0} dB)");
    }
    return;
}

if (args.Length > 0 && args[0] == "cabletest")
{
    // Plays a quiet 1 kHz tone into CABLE Input and measures what arrives on CABLE Output.
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var cabIn = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));
    var cabOut = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"play -> {cabIn.FriendlyName} (volume {cabIn.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%, muted {cabIn.AudioEndpointVolume.Mute})");
    Console.WriteLine($"rec  <- {cabOut.FriendlyName} (volume {cabOut.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%, muted {cabOut.AudioEndpointVolume.Mute})");

    using var cap = new NAudio.CoreAudioApi.WasapiCapture(cabOut);
    int ch = cap.WaveFormat.Channels; float peak = 0;
    cap.DataAvailable += (_, e) =>
    {
        for (int i = 0; i + 4 <= e.BytesRecorded; i += 4) peak = MathF.Max(peak, MathF.Abs(BitConverter.ToSingle(e.Buffer, i)));
    };
    var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 1000, Gain = 0.1, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin };
    using var player = new NAudio.Wave.WasapiOut(cabIn, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
    player.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(tone));
    cap.StartRecording();
    player.Play();
    for (int t = 1; t <= 4; t++) { Thread.Sleep(1000); Console.WriteLine($"  t={t}s  CABLE Output peak {Db.ToDb(peak):0.0} dBFS  (tone sent at -20 dBFS)"); peak = 0; }
    player.Stop(); cap.StopRecording();
    return;
}

if (args.Length > 0 && args[0] == "tune")
{
    var names = new[] { "silence", "typing", "whisper", "speech", "shout" };
    var audio = names.ToDictionary(n => n, n =>
    {
        using var r = new NAudio.Wave.WaveFileReader($"calibration/{n}.wav");
        var l = new List<float>(); float[] f;
        while ((f = r.ReadNextSampleFrame()) is { Length: > 0 }) l.Add(f[0]);
        return l.ToArray();
    });
    const int Sr = 48000, Win = Sr / 50;

    float Pct(float[] s, float q)
    {
        var l = new List<float>();
        for (int k = 0; k + Win <= s.Length; k += Win)
        {
            double a = 0; for (int j = 0; j < Win; j++) a += s[k + j] * s[k + j];
            l.Add(Db.ToDb((float)Math.Sqrt(a / Win)));
        }
        l.Sort(); return l[Math.Min(l.Count - 1, (int)(q * l.Count))];
    }

    (float gap, float typingP90, float whisperP50, double whisperOpen, double typingOpen, double speechOpen) Run(float open, float close, float hold, float reduction)
    {
        var chain = new ProcessingChain(Sr);
        chain.Gate.OpenThresholdDb = open; chain.Gate.CloseThresholdDb = close;
        chain.Gate.HoldMs = hold; chain.Gate.ReductionDb = reduction;
        var open_ = new Dictionary<string, double>(); var outp = new Dictionary<string, float[]>();
        foreach (var n in names)
        {
            var o = (float[])audio[n].Clone(); int ob = 0, tb = 0;
            for (int i = 0; i + 480 <= o.Length; i += 480) { chain.Process(o.AsSpan(i, 480)); tb++; if (chain.Gate.IsOpen) ob++; }
            outp[n] = o; open_[n] = 100.0 * ob / tb;
        }
        float t90 = Pct(outp["typing"], .9f), w50 = Pct(outp["whisper"], .5f);
        return (w50 - t90, t90, w50, open_["whisper"], open_["typing"], open_["speech"]);
    }

    var cur = Run(-50, -55, 100, -30);
    Console.WriteLine($"current (-50/-55, hold 100, -30 dB): gap {cur.gap:0.0} dB  typing p90 {cur.typingP90:0.0}  whisper p50 {cur.whisperP50:0.0}  whisper open {cur.whisperOpen:0}%  typing open {cur.typingOpen:0}%");

    var rows = new List<(float o, float c, float h, float r, (float gap, float typingP90, float whisperP50, double whisperOpen, double typingOpen, double speechOpen) m)>();
    for (float o = -60; o <= -36; o += 2)
        foreach (float dc in new[] { 3f, 5f, 8f })
            foreach (float h in new[] { 30f, 60f, 100f, 150f })
                foreach (float r in new[] { -30f, -40f })
                    rows.Add((o, o - dc, h, r, Run(o, o - dc, h, r)));

    Console.WriteLine("\nbest settings with whisper gate-open >= 80% and speech gate-open >= 99%:");
    Console.WriteLine("  open  close  hold  reduct |  gap  typing p90  whisper p50  whisper open  typing open");
    foreach (var x in rows.Where(x => x.m.whisperOpen >= 80 && x.m.speechOpen >= 99).OrderByDescending(x => x.m.gap).Take(10))
        Console.WriteLine($"  {x.o,4:0}  {x.c,5:0}  {x.h,4:0}  {x.r,5:0}  | {x.m.gap,4:0.0}  {x.m.typingP90,10:0.0}  {x.m.whisperP50,11:0.0}  {x.m.whisperOpen,11:0}%  {x.m.typingOpen,10:0}%");
    return;
}

if (args.Length > 0 && args[0] == "offline")
{
    // Runs calibration/*.wav (in recorded order, one shared chain) through the processing chain.
    var names = new[] { "silence", "typing", "whisper", "speech", "shout" };
    ProcessingChain? chain = null;
    Console.WriteLine($"{"phase",-8} | {"in p50",7} {"in p90",7} | {"out p50",8} {"out p90",8} {"out max",8} | gate open");
    foreach (var name in names)
    {
        using var reader = new NAudio.Wave.WaveFileReader($"calibration/{name}.wav");
        if (chain is null)
        {
            chain = new ProcessingChain(reader.WaveFormat.SampleRate);
            var rnn = args.FirstOrDefault(a => a.StartsWith("rnn="))?.Substring(4);
            if (chain.Rnnoise is not null)
            {
                if (rnn is null || rnn == "off") chain.Rnnoise.Enabled = false;
                else if (rnn == "adaptive") chain.Rnnoise.Adaptive = true;
                else { chain.Rnnoise.Adaptive = false; chain.Rnnoise.Strength = float.Parse(rnn, System.Globalization.CultureInfo.InvariantCulture); }
            }
            Console.WriteLine($"RNNoise: {(chain.Rnnoise is not { Enabled: true } ? "off" : chain.Rnnoise.Adaptive ? "adaptive" : $"fixed strength {chain.Rnnoise.Strength:0.00}")}");
        }
        var samples = new List<float>();
        float[] frame;
        while ((frame = reader.ReadNextSampleFrame()) is { Length: > 0 }) samples.Add(frame[0]);
        var input = samples.ToArray();
        var output = (float[])input.Clone();

        int block = 480, win = reader.WaveFormat.SampleRate / 50;
        int openBlocks = 0, totalBlocks = 0;
        var vps = new List<float>();
        for (int i = 0; i + block <= output.Length; i += block)
        {
            chain.Process(output.AsSpan(i, block));
            totalBlocks++; if (chain.Gate.IsOpen) openBlocks++;
            if (chain.Rnnoise is { Enabled: true }) vps.Add(chain.Rnnoise.VoiceProbability);
        }
        if (vps.Count > 0)
        {
            vps.Sort();
            Console.WriteLine($"   RNNoise voice probability  p25 {vps[vps.Count / 4]:0.00}  p50 {vps[vps.Count / 2]:0.00}  p75 {vps[vps.Count * 3 / 4]:0.00}  p90 {vps[vps.Count * 9 / 10]:0.00}  frames>0.5: {100.0 * vps.Count(v => v > 0.5f) / vps.Count:0}%");
        }

        List<float> Levels(float[] s)
        {
            var l = new List<float>();
            for (int k = 0; k + win <= s.Length; k += win)
            {
                double a = 0; for (int j = 0; j < win; j++) a += s[k + j] * s[k + j];
                l.Add(Db.ToDb((float)Math.Sqrt(a / win)));
            }
            l.Sort(); return l;
        }
        float P(List<float> l, float q) => l[Math.Min(l.Count - 1, (int)(q * l.Count))];
        var li = Levels(input); var lo = Levels(output);
        Console.WriteLine($"{name,-8} | {P(li, .5f),7:0.0} {P(li, .9f),7:0.0} | {P(lo, .5f),8:0.0} {P(lo, .9f),8:0.0} {lo[^1],8:0.0} | {100.0 * openBlocks / totalBlocks,5:0}%");

        using var w = new NAudio.Wave.WaveFileWriter($"calibration/out_{name}.wav", NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(reader.WaveFormat.SampleRate, 1));
        w.WriteSamples(output, 0, output.Length);
    }
    return;
}

if (args.Length > 0 && args[0] == "calibrate")
{
    // Measures 20 ms RMS windows on channel 2 for five scripted phases and suggests gate thresholds.
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var dev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
    using var cap = new NAudio.CoreAudioApi.WasapiCapture(dev);
    int ch = cap.WaveFormat.Channels;
    int chIdx = args.Contains("ch1") ? 0 : 1;
    int win = cap.WaveFormat.SampleRate / 50; // 20 ms
    var windows = new List<float>();
    double acc = 0; int n = 0; bool recording = false;
    NAudio.Wave.WaveFileWriter? wav = null;
    Directory.CreateDirectory("calibration");
    cap.DataAvailable += (_, e) =>
    {
        for (int i = 0; i + 4 * ch <= e.BytesRecorded; i += 4 * ch)
        {
            float v = BitConverter.ToSingle(e.Buffer, i + 4 * chIdx);
            if (recording) wav?.WriteSample(v);
            acc += v * v;
            if (++n == win)
            {
                if (recording) lock (windows) windows.Add(Db.ToDb(MathF.Sqrt((float)(acc / win))));
                acc = 0; n = 0;
            }
        }
    };

    var phases = new (string Name, string Prompt)[]
    {
        ("silence",  "Stay completely quiet. Hands off keyboard and mouse."),
        ("typing",   "Type normally on your keyboard and click the mouse. Do NOT speak."),
        ("whisper",  "Whisper a sentence, pause, whisper again. Keep repeating."),
        ("speech",   "Speak at your normal podcast volume. Keep talking."),
        ("shout",    "Shout a few times, as loud as you ever would."),
    };
    var stats = new Dictionary<string, float[]>();
    cap.StartRecording();
    foreach (var (name, prompt) in phases)
    {
        recording = false;
        Console.WriteLine();
        Console.WriteLine($"[{name}] {prompt}  (starts in 3 s)");
        Thread.Sleep(3000);
        lock (windows) windows.Clear();
        wav = new NAudio.Wave.WaveFileWriter($"calibration/{name}.wav", NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(cap.WaveFormat.SampleRate, 1));
        recording = true;
        Console.WriteLine("  ...recording 6 s");
        Thread.Sleep(6000);
        recording = false;
        Thread.Sleep(50);
        wav.Dispose(); wav = null;
        float[] w; lock (windows) w = windows.OrderBy(x => x).ToArray();
        stats[name] = w;
        float P(float q) => w[Math.Min(w.Length - 1, (int)(q * w.Length))];
        Console.WriteLine($"  RMS dBFS  p10 {P(.1f):0.0}   p50 {P(.5f):0.0}   p90 {P(.9f):0.0}   p99 {P(.99f):0.0}   max {w[^1]:0.0}");
    }
    cap.StopRecording();

    float Q(string k, float q) => stats[k][Math.Min(stats[k].Length - 1, (int)(q * stats[k].Length))];
    float noiseTop = MathF.Max(Q("silence", .99f), Q("typing", .90f));   // loudest thing we want gated out
    float voiceBottom = Q("whisper", .75f);                              // quietest thing we want kept (the loud half of whispers)
    Console.WriteLine();
    Console.WriteLine($"Noise ceiling (silence p99 / typing p90): {noiseTop:0.0} dBFS");
    Console.WriteLine($"Whisper level (p75):                       {voiceBottom:0.0} dBFS");
    Console.WriteLine($"Normal speech p50 {Q("speech", .5f):0.0}, shout max {stats["shout"][^1]:0.0}");
    float gap = voiceBottom - noiseTop;
    if (gap < 6f)
        Console.WriteLine($"WARNING: only {gap:0.0} dB between typing and whispers - a level gate alone cannot separate them. Raise the Scarlett gain and/or move closer to the mic.");
    else
        Console.WriteLine($"Suggested gate: open {noiseTop + gap * 0.6f:0.0} dBFS, close {noiseTop + gap * 0.35f:0.0} dBFS  (gap {gap:0.0} dB)");
    return;
}

if (args.Length > 0 && args[0] == "meter")
{
    // Compares Windows' own meter for the Focusrite with what a raw WASAPI capture sees, per second.
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var dev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
    using var cap = new NAudio.CoreAudioApi.WasapiCapture(dev);
    int ch = cap.WaveFormat.Channels;
    var peaks = new float[ch];
    cap.DataAvailable += (_, e) =>
    {
        for (int i = 0; i + 4 * ch <= e.BytesRecorded; i += 4 * ch)
            for (int c = 0; c < ch; c++)
                peaks[c] = MathF.Max(peaks[c], MathF.Abs(BitConverter.ToSingle(e.Buffer, i + 4 * c)));
    };
    cap.StartRecording();
    Console.WriteLine("SPEAK NOW, loudly and close to the mic, for 10 seconds.");
    Console.WriteLine("  windows meter   my capture ch1   my capture ch2");
    for (int t = 0; t < 10; t++)
    {
        Array.Clear(peaks);
        Thread.Sleep(1000);
        Console.WriteLine($"  {Db.ToDb(dev.AudioMeterInformation.MasterPeakValue),9:0.0} dB   {Db.ToDb(peaks[0]),11:0.0} dB   {Db.ToDb(peaks[1]),11:0.0} dB");
    }
    cap.StopRecording();
    return;
}

if (args.Length > 0 && args[0] == "diag")
{
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    foreach (var dev in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
    {
        var vol = dev.AudioEndpointVolume;
        Console.WriteLine($"{dev.FriendlyName}");
        Console.WriteLine($"  Windows volume {vol.MasterVolumeLevelScalar * 100:0}%  muted={vol.Mute}  mix format={dev.AudioClient.MixFormat}");
        using var cap = new NAudio.CoreAudioApi.WasapiCapture(dev);
        var ch = cap.WaveFormat.Channels;
        var peaks = new float[ch];
        cap.DataAvailable += (_, e) =>
        {
            for (int i = 0; i + 4 * ch <= e.BytesRecorded; i += 4 * ch)
                for (int c = 0; c < ch; c++)
                    peaks[c] = MathF.Max(peaks[c], MathF.Abs(BitConverter.ToSingle(e.Buffer, i + 4 * c)));
        };
        cap.StartRecording();
        Thread.Sleep(3000);
        cap.StopRecording();
        Console.WriteLine($"  format={cap.WaveFormat}  peak per channel (dBFS): {string.Join(", ", peaks.Select(p => Db.ToDb(p).ToString("0.0")))}");
    }
    return;
}

if (args.Length == 0 || args[0] != "run")
{
    Console.WriteLine("INPUTS");
    foreach (var d in inputs) Console.WriteLine($"  {d.Name}");
    Console.WriteLine("OUTPUTS");
    foreach (var d in outputs) Console.WriteLine($"  {d.Name}");
    return;
}

var mic = inputs.FirstOrDefault(d => d.Name.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
var cable = outputs.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));
if (mic is null || cable is null)
{
    Console.WriteLine($"Missing device. mic={mic?.Name ?? "none"} cable={cable?.Name ?? "none"}");
    return;
}

int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20;
bool bypass = args.Contains("bypass");
// Defaults to input 2 (index 1) of the interface, where the author's microphone is plugged in. Pass "ch1" for input 1.
int channel = args.Contains("ch1") ? 0 : 1;

// ASIO drivers are COM objects that need a single-threaded apartment, like the real app's UI thread.
var worker = new Thread(() =>
{
using var engine = new AudioEngine();
engine.Start(mic.Id, cable.Id, channel, args.Contains("asio") ? LatencyMode.Lowest : LatencyMode.Standard);
engine.Chain!.Bypass = bypass;
Console.WriteLine($"input mode: {engine.ActiveMode}{(engine.ModeNote is null ? "" : "  (note: " + engine.ModeNote + ")")}");
if (engine.Latency is { } l0)
    Console.WriteLine($"estimated delay: input {l0.InputMs:0.0} ms [{l0.InputPath}] + processing {l0.ProcessingMs:0.0} ms + buffer {l0.BufferMs:0.0} ms + output {l0.OutputMs:0.0} ms = {l0.TotalMs:0.0} ms (plus VB-Cable's own buffering)");

// Independently listen on CABLE Output (what Discord/OBS would receive), to prove audio arrives there.
using var en2 = new NAudio.CoreAudioApi.MMDeviceEnumerator();
var cableOut = en2.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
    .FirstOrDefault(d => d.FriendlyName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));
float cablePeak = 0;
using var cableCap = cableOut is null ? null : new NAudio.CoreAudioApi.WasapiCapture(cableOut);
if (cableCap is not null)
{
    cableCap.DataAvailable += (_, e) =>
    {
        for (int i = 0; i + 4 <= e.BytesRecorded; i += 4) cablePeak = MathF.Max(cablePeak, MathF.Abs(BitConverter.ToSingle(e.Buffer, i)));
    };
    cableCap.StartRecording();
}

Console.WriteLine($"{mic.Name} ch{channel + 1} -> {cable.Name}  ({(bypass ? "BYPASS" : "processed")}, {seconds}s)");
Console.WriteLine("  in dBFS   out dBFS   CABLE Output   gate  comp GR   leveler");

var end = DateTime.UtcNow.AddSeconds(seconds);
int tickNo = 0;
while (DateTime.UtcNow < end)
{
    Thread.Sleep(250);
    if (++tickNo % 4 == 0 && engine.TakeBufferStats() is { } st)
        Console.WriteLine($"      [buffer 1 s] captured {st.Written} frames in {st.Writes} blocks, played {st.Read}; waiting just before a block: min {st.MinFill}, max {st.MaxFill} frames; glitches so far {engine.Underruns}");
    var c = engine.Chain!;
    float cp = cablePeak; cablePeak = 0;
    Console.WriteLine($"  {Db.ToDb(engine.InputPeak),7:0.0}   {Db.ToDb(engine.OutputPeak),8:0.0}   {Db.ToDb(cp),12:0.0}   {(c.Gate.IsOpen ? "open  " : "closed")} {c.Compressor.GainReductionDb,7:0.0}   {c.Leveler.CurrentGainDb,6:0.0}");
}
cableCap?.StopRecording();
Console.WriteLine($"audio glitches (buffer underruns) during this run: {engine.Underruns}; final cushion {engine.Latency?.BufferMs:0.0} ms");
});
worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();

internal static class Timer1ms
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    public static void Enable() => timeBeginPeriod(1);
}

/// <summary>Emits a short click every 600 ms from inside the player callback, reporting the moment it hands the buffer over.</summary>
internal sealed class ClickSource : NAudio.Wave.IWaveProvider
{
    private readonly int _channels;
    private readonly Action _onClick;
    private long _frames;
    public ClickSource(NAudio.Wave.WaveFormat format, Action onClick) { WaveFormat = format; _channels = format.Channels; _onClick = onClick; }
    public NAudio.Wave.WaveFormat WaveFormat { get; }

    public int Read(Span<byte> destination)
    {
        destination.Clear();
        var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(destination);
        int frames = destination.Length / (4 * _channels);
        for (int i = 0; i < frames; i++)
        {
            long n = _frames + i;
            if (n > 48000 * 2 && n % 28800 == 0)     // every 600 ms after a 2 s warm-up
            {
                _onClick();
                for (int k = 0; k < 24 && i + k < frames; k++)
                    for (int c = 0; c < _channels; c++) f[(i + k) * _channels + c] = 0.8f * MathF.Sin(2 * MathF.PI * 1000f * k / 48000f + 1.0f);
            }
        }
        _frames += frames;
        return destination.Length;
    }
}

/// <summary>Plays silence and reports how many frames each callback asked for.</summary>
internal sealed class CountingSource : NAudio.Wave.IWaveProvider
{
    private readonly int _channels;
    private readonly Action<int> _onRead;
    public CountingSource(NAudio.Wave.WaveFormat format, Action<int> onRead) { WaveFormat = format; _channels = format.Channels; _onRead = onRead; }
    public NAudio.Wave.WaveFormat WaveFormat { get; }
    public int Read(Span<byte> destination) { destination.Clear(); _onRead(destination.Length / (4 * _channels)); return destination.Length; }
}
