using MicOptimizer.Core.Dsp;
using Xunit;

namespace MicOptimizer.Tests;

public class DynamicsTests
{
    private const int Sr = 48000;

    private static float[] Sine(float db, int samples, float freq = 300f)
    {
        float amp = Db.ToLinear(db);
        var b = new float[samples];
        for (int i = 0; i < samples; i++) b[i] = amp * MathF.Sin(2 * MathF.PI * freq * i / Sr);
        return b;
    }

    private static float PeakDb(ReadOnlySpan<float> s)
    {
        float p = 0;
        foreach (var v in s) p = MathF.Max(p, MathF.Abs(v));
        return Db.ToDb(p);
    }

    [Fact]
    public void Compressor_reduces_a_loud_signal_by_roughly_the_ratio()
    {
        var comp = new Compressor(Sr) { ThresholdDb = -20f, Ratio = 4f, KneeDb = 0f, AttackMs = 1f, ReleaseMs = 50f };
        var buf = Sine(0f, Sr);        // 20 dB over threshold
        comp.Process(buf);
        // Expect ~ -20 + 20/4 = -15 dB peak.
        Assert.InRange(PeakDb(buf.AsSpan(Sr / 2)), -16f, -14f);
    }

    [Fact]
    public void Compressor_leaves_quiet_signals_alone()
    {
        var comp = new Compressor(Sr) { ThresholdDb = -20f, Ratio = 4f };
        var buf = Sine(-40f, Sr);
        comp.Process(buf);
        Assert.InRange(PeakDb(buf.AsSpan(Sr / 2)), -40.5f, -39.5f);
    }

    [Fact]
    public void AutoLeveler_brings_a_whisper_and_a_loud_voice_closer_together()
    {
        // Wide range here to test the mechanism; the defaults are deliberately narrower.
        var whisper = new AutoLeveler(Sr) { TargetDb = -20f, MaxBoostDb = 15f, MaxCutDb = -12f };
        var whisperBuf = Sine(-38f, Sr * 8);
        whisper.Process(whisperBuf);

        var loud = new AutoLeveler(Sr) { TargetDb = -20f, MaxBoostDb = 15f, MaxCutDb = -12f };
        var loudBuf = Sine(-8f, Sr * 8);
        loud.Process(loudBuf);

        float whisperOut = PeakDb(whisperBuf.AsSpan(Sr * 7));
        float loudOut = PeakDb(loudBuf.AsSpan(Sr * 7));
        // Input gap was 30 dB; the leveler should shrink it a lot.
        Assert.True(loudOut - whisperOut < 12f, $"gap was {loudOut - whisperOut} dB");
    }

    [Fact]
    public void AutoLeveler_does_not_boost_room_noise()
    {
        var lev = new AutoLeveler(Sr) { FreezeBelowDb = -55f };
        var noise = Sine(-65f, Sr * 4);
        lev.Process(noise);
        Assert.InRange(PeakDb(noise.AsSpan(Sr * 3)), -66f, -64f);
    }
}
