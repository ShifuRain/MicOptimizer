using MicOptimizer.Core.Dsp;
using Xunit;

namespace MicOptimizer.Tests;

public class ToneTests
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

    /// <summary>Gain in dB the stage applies to a steady tone (measured after it settles).</summary>
    private static float GainAt(IAudioStage stage, float freq)
    {
        var buf = Sine(freq, -20f, Sr);
        stage.Process(buf);
        return Db.ToDb(Rms(buf.AsSpan(Sr / 2))) - Db.ToDb(Db.ToLinear(-20f) * 0.7071f);
    }

    [Fact]
    public void Equalizer_cuts_mud_boosts_presence_and_leaves_the_middle_alone()
    {
        Assert.InRange(GainAt(new Equalizer(Sr) { MudDb = -3f }, 250f), -3.3f, -2.7f);
        Assert.InRange(GainAt(new Equalizer(Sr) { PresenceDb = 2f }, 3500f), 1.7f, 2.4f);
        Assert.InRange(GainAt(new Equalizer(Sr), 1000f), -0.7f, 1.0f);
    }

    [Fact]
    public void Equalizer_air_shelf_lifts_high_frequencies()
    {
        // Isolate the shelf: the other two bands are zeroed (the default mud cut still reaches 500 Hz).
        Assert.InRange(GainAt(new Equalizer(Sr) { AirDb = 3f, PresenceDb = 0f, MudDb = 0f }, 16000f), 2.5f, 3.5f);
        Assert.InRange(GainAt(new Equalizer(Sr) { AirDb = 3f, PresenceDb = 0f, MudDb = 0f }, 500f), -0.3f, 0.3f);
    }

    [Fact]
    public void DeEsser_turns_down_a_dominant_high_band_only_when_strength_is_on()
    {
        float[] SibilantBurst()
        {
            var low = Sine(300f, -30f, Sr);
            var high = Sine(8000f, -12f, Sr);      // far louder than the low band: an "sss"
            for (int i = 0; i < low.Length; i++) low[i] += high[i];
            return low;
        }

        var on = SibilantBurst();
        new DeEsser(Sr) { Strength = 1f }.Process(on);
        var off = SibilantBurst();
        new DeEsser(Sr) { Strength = 0f }.Process(off);

        float onDb = Db.ToDb(Rms(on.AsSpan(Sr / 2))), offDb = Db.ToDb(Rms(off.AsSpan(Sr / 2)));
        Assert.True(offDb - onDb > 3f, $"reduction only {offDb - onDb:0.0} dB");
    }

    [Fact]
    public void DeEsser_leaves_speech_without_sibilance_at_the_same_level()
    {
        // The crossover only shifts phase (all-pass), so compare levels, not samples.
        foreach (float f in new[] { 300f, 1000f, 3000f })
            Assert.InRange(GainAt(new DeEsser(Sr) { Strength = 1f }, f), -0.3f, 0.3f);
    }
}
