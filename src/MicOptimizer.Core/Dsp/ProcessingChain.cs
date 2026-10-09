namespace MicOptimizer.Core.Dsp;

/// <summary>The ordered podcast chain: HPF -> RNNoise -> gate -> auto-level -> compressor -> limiter.</summary>
public sealed class ProcessingChain : IDisposable
{
    public ProcessingChain(int sampleRate)
    {
        SampleRate = sampleRate;
        HighPass = new HighPassFilter(sampleRate);
        // RNNoise only runs at 48 kHz; at other rates the stage is simply absent.
        Rnnoise = sampleRate == 48000 ? new RnnoiseStage(sampleRate) : null;
        Gate = new Gate(sampleRate);
        Eq = new Equalizer(sampleRate);
        Leveler = new AutoLeveler(sampleRate);
        Compressor = new Compressor(sampleRate) { ThresholdDb = -16f, Ratio = 2f, MakeupDb = 2f };
        DeEsser = new DeEsser(sampleRate);
        Limiter = new Limiter(sampleRate);

        var stages = new List<IAudioStage> { HighPass };
        if (Rnnoise is not null) stages.Add(Rnnoise);
        stages.AddRange(new IAudioStage[] { Gate, Eq, Leveler, Compressor, DeEsser, Limiter });
        Stages = stages;
    }

    public int SampleRate { get; }
    public HighPassFilter HighPass { get; }
    public RnnoiseStage? Rnnoise { get; }
    public Gate Gate { get; }
    public Equalizer Eq { get; }
    public AutoLeveler Leveler { get; }
    public Compressor Compressor { get; }
    public DeEsser DeEsser { get; }
    public Limiter Limiter { get; }
    public IReadOnlyList<IAudioStage> Stages { get; }

    /// <summary>Global bypass: audio passes through untouched.</summary>
    public bool Bypass { get; set; }

    /// <summary>
    /// Delay the chain adds, in milliseconds: one 10 ms RNNoise frame while noise removal is on, plus the
    /// limiter's lookahead. Nothing else in the chain looks ahead.
    /// </summary>
    public double LatencyMs
    {
        get
        {
            if (Bypass) return 0;
            double samples = (Rnnoise is { Enabled: true } r ? r.LatencySamples : 0)
                             + (Limiter.Enabled ? Limiter.LatencySamples : 0);
            return samples * 1000.0 / SampleRate;
        }
    }

    public float InputGain { get; set; } = 1f;

    public void Process(Span<float> buffer)
    {
        if (Bypass) return;
        if (InputGain != 1f)
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= InputGain;
        // Whispers are naturally hissy, so the de-esser only acts while RNNoise hears normal/loud speech.
        // (No detector available -> no de-essing.)
        DeEsser.Strength = Rnnoise is { Enabled: true } ? Rnnoise.Blend : 0f;
        foreach (var stage in Stages) stage.Process(buffer);
    }

    public void Dispose() => Rnnoise?.Dispose();
}
