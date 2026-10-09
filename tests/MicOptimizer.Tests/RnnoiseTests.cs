using MicOptimizer.Core.Dsp;
using Xunit;

namespace MicOptimizer.Tests;

public class RnnoiseTests
{
    private const int Sr = 48000;

    private static float Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s) sum += v * v;
        return (float)Math.Sqrt(sum / s.Length);
    }

    [Fact]
    public void Rejects_non_48k_sample_rates()
    {
        Assert.Throws<ArgumentException>(() => new RnnoiseStage(44100));
    }

    private static float[] Hiss(int samples)
    {
        var rng = new Random(1);
        var buf = new float[samples];
        for (int i = 0; i < buf.Length; i++) buf[i] = (float)(rng.NextDouble() * 2 - 1) * 0.02f; // ~-41 dBFS
        return buf;
    }

    /// <summary>Feeds uneven block sizes to exercise the frame buffering.</summary>
    private static void ProcessUneven(RnnoiseStage stage, float[] buf)
    {
        int pos = 0, step = 137;
        while (pos < buf.Length)
        {
            int n = Math.Min(step, buf.Length - pos);
            stage.Process(buf.AsSpan(pos, n));
            pos += n;
            step = step == 137 ? 700 : 137;
        }
    }

    [Fact]
    public void Fixed_full_strength_cleans_noise_that_is_not_speech_like()
    {
        // Synthetic white hiss sounds like a whispered "shh" to RNNoise, so it is NOT strongly reduced.
        // This only pins that the cleaned path is wired up and changes the signal at full strength.
        using var wet = new RnnoiseStage(Sr) { Adaptive = false, Strength = 1f };
        using var dry = new RnnoiseStage(Sr) { Adaptive = false, Strength = 0f };
        var a = Hiss(Sr * 2); var b = (float[])a.Clone();
        ProcessUneven(wet, a); ProcessUneven(dry, b);
        Assert.NotEqual(Rms(a.AsSpan(Sr)), Rms(b.AsSpan(Sr)));
    }

    [Fact]
    public void Adaptive_mode_passes_non_speech_through_untouched_so_whispers_survive()
    {
        using var stage = new RnnoiseStage(Sr);   // adaptive by default
        // Very quiet, slow-moving low-frequency rumble: nothing speech-like for the detector to latch onto.
        // (RNNoise's detector does fire on pure tones and white hiss, so those make poor "non-speech" inputs.)
        var input = new float[Sr * 2];
        for (int i = 0; i < input.Length; i++) input[i] = 0.0005f * MathF.Sin(2 * MathF.PI * 8f * i / Sr);
        var buf = (float[])input.Clone();
        float maxBlend = 0, maxVp = 0;
        for (int pos = 0; pos < buf.Length; pos += 480)
        {
            stage.Process(buf.AsSpan(pos, 480));
            maxBlend = MathF.Max(maxBlend, stage.Blend);
            maxVp = MathF.Max(maxVp, stage.VoiceProbability);
        }
        Assert.True(maxBlend < 0.001f, $"max blend {maxBlend}, max voice probability {maxVp}");
        for (int i = Sr; i < buf.Length; i++)
            Assert.Equal(input[i - RnnoiseStage.FrameSize], buf[i], 5);
    }

    [Fact]
    public void Strength_zero_is_a_pure_one_frame_delay()
    {
        using var stage = new RnnoiseStage(Sr) { Adaptive = false, Strength = 0f };
        var input = new float[RnnoiseStage.FrameSize * 4];
        for (int i = 0; i < input.Length; i++) input[i] = MathF.Sin(i * 0.05f) * 0.3f;
        var buf = (float[])input.Clone();
        stage.Process(buf);
        for (int i = RnnoiseStage.FrameSize; i < buf.Length; i++)
            Assert.Equal(input[i - RnnoiseStage.FrameSize], buf[i], 5);
    }
}
