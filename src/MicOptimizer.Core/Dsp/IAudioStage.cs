namespace MicOptimizer.Core.Dsp;

/// <summary>A mono in-place DSP stage running at a fixed sample rate.</summary>
public interface IAudioStage
{
    bool Enabled { get; set; }
    void Process(Span<float> buffer);
}

public static class Db
{
    public static float ToLinear(float db) => MathF.Pow(10f, db / 20f);
    public static float ToDb(float linear) => 20f * MathF.Log10(MathF.Max(linear, 1e-9f));
    /// <summary>One-pole smoothing coefficient for a time constant in milliseconds.</summary>
    public static float TimeCoeff(float ms, int sampleRate) => MathF.Exp(-1f / (MathF.Max(ms, 0.01f) * 0.001f * sampleRate));
}
