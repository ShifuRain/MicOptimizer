using MicOptimizer.Core.Dsp;
using Xunit;

namespace MicOptimizer.Tests;

public class DspTests
{
    private const int Sr = 48000;

    private static float[] Sine(float freq, float db, int samples)
    {
        float amp = Db.ToLinear(db);
        var b = new float[samples];
        for (int i = 0; i < samples; i++) b[i] = amp * MathF.Sin(2 * MathF.PI * freq * i / Sr);
        return b;
    }

    private static float Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s) sum += v * v;
        return (float)Math.Sqrt(sum / s.Length);
    }

    [Fact]
    public void HighPass_attenuates_rumble_and_passes_voice()
    {
        var low = Sine(30f, -10f, Sr);
        new HighPassFilter(Sr, 90f).Process(low);
        var voice = Sine(1000f, -10f, Sr);
        new HighPassFilter(Sr, 90f).Process(voice);

        float expected = Db.ToLinear(-10f) * 0.707f;
        Assert.True(Rms(low.AsSpan(Sr / 2)) < expected * 0.2f);
        Assert.InRange(Rms(voice.AsSpan(Sr / 2)), expected * 0.95f, expected * 1.05f);
    }

    [Fact]
    public void Gate_closes_on_quiet_noise_and_opens_on_speech()
    {
        var gate = new Gate(Sr) { OpenThresholdDb = -40f, CloseThresholdDb = -45f, ReductionDb = -30f };
        var noise = Sine(500f, -55f, Sr);
        gate.Process(noise);
        Assert.False(gate.IsOpen);
        Assert.True(Rms(noise.AsSpan(Sr / 2)) < Db.ToLinear(-55f) * 0.707f * 0.1f);

        var speech = Sine(500f, -20f, Sr);
        gate.Process(speech);
        Assert.True(gate.IsOpen);
        float expected = Db.ToLinear(-20f) * 0.707f;
        Assert.InRange(Rms(speech.AsSpan(Sr / 2)), expected * 0.95f, expected * 1.05f);
    }

    [Fact]
    public void Gate_ignores_loud_low_frequency_thumps_but_opens_for_voice_band()
    {
        var gate = new Gate(Sr) { OpenThresholdDb = -40f, CloseThresholdDb = -45f };
        gate.Process(Sine(60f, -15f, Sr));      // desk thump: loud, but below the sidechain band
        Assert.False(gate.IsOpen);

        gate.Process(Sine(800f, -30f, Sr));     // quieter, but squarely in the voice band
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void Limiter_never_exceeds_ceiling_even_on_a_sudden_scream()
    {
        var lim = new Limiter(Sr) { CeilingDb = -1f };
        var buf = new float[Sr];
        Sine(300f, -30f, Sr / 2).CopyTo(buf, 0);
        Sine(300f, 6f, Sr / 2).CopyTo(buf, Sr / 2); // overdriven input
        lim.Process(buf);

        float ceiling = Db.ToLinear(-1f);
        foreach (var v in buf) Assert.True(MathF.Abs(v) <= ceiling + 1e-4f);
    }
}
