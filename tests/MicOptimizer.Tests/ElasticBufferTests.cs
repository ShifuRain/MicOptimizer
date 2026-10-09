using MicOptimizer.Audio;
using Xunit;

namespace MicOptimizer.Tests;

public class ElasticBufferTests
{
    private static float[] Ramp(int start, int count)
    {
        var b = new float[count];
        for (int i = 0; i < count; i++) b[i] = (start + i) / 100000f;
        return b;
    }

    /// <summary>Capture delivers `capture` frames per call and render pulls `render` frames per call, at the same long-run rate.</summary>
    private static (ElasticBuffer buffer, List<int> fills, float[] played) Simulate(
        int cushion, int capture, int render, int totalFrames, double renderClockRatio = 1.0)
    {
        var buf = new ElasticBuffer(cushion);
        var fills = new List<int>();
        var played = new List<float>();
        int written = 0;
        double renderDebt = 0;
        var outBlock = new float[render];

        // Event-driven: one capture write per `capture` frames of time, render reads as time passes.
        for (int t = 0; t < totalFrames; t += render)
        {
            while (written * 1.0 <= t * renderClockRatio)
            {
                buf.Write(Ramp(written, capture));
                written += capture;
            }
            buf.Read(outBlock);
            played.AddRange(outBlock);
            fills.Add(buf.Count);
            renderDebt += 0;
        }
        return (buf, fills, played.ToArray());
    }

    [Fact]
    public void Plays_nothing_until_the_cushion_has_built_up_then_plays_in_order()
    {
        var buf = new ElasticBuffer(cushionFrames: 96);
        var o = new float[48];

        buf.Write(Ramp(0, 48));
        buf.Read(o);
        Assert.All(o, v => Assert.Equal(0f, v));          // still priming

        buf.Write(Ramp(48, 480));
        buf.Read(o);
        Assert.Equal(0f / 100000f, o[0], 6);              // audio starts from the very first sample
        Assert.Equal(47f / 100000f, o[47], 6);
    }

    [Fact]
    public void Steady_clocks_never_underrun_and_keep_a_small_constant_delay()
    {
        // 10 ms capture packets, 2 ms render periods, 2 ms cushion.
        var (buf, fills, _) = Simulate(cushion: 96, capture: 480, render: 96, totalFrames: 48000 * 20);
        Assert.Equal(0, buf.Underruns);
        Assert.True(fills.Max() <= 96 + 480 + 96, $"fill grew to {fills.Max()} frames");
    }

    [Fact]
    public void A_render_clock_that_runs_slightly_slow_does_not_let_the_delay_grow()
    {
        // Capture produces 0.05% more audio than render consumes: without correction the queue would swell.
        var (buf, fills, _) = Simulate(cushion: 96, capture: 480, render: 96, totalFrames: 48000 * 120, renderClockRatio: 1.0005);
        int lateMax = fills.Skip(fills.Count * 3 / 4).Max();
        Assert.True(lateMax <= 96 + 480 + 150, $"delay grew to {lateMax} frames after drift");
        Assert.Equal(0, buf.Underruns);
    }

    [Fact]
    public void A_render_clock_that_runs_slightly_fast_does_not_run_dry()
    {
        var (buf, _, _) = Simulate(cushion: 96, capture: 480, render: 96, totalFrames: 48000 * 120, renderClockRatio: 0.9995);
        Assert.True(buf.Underruns <= 1, $"{buf.Underruns} underruns");
    }

    [Fact]
    public void Corrections_are_tiny_so_a_steady_tone_stays_clean()
    {
        // Feed a 440 Hz tone with a drifting clock and make sure no sample jumps by more than a small step.
        var buf = new ElasticBuffer(96);
        var played = new List<float>();
        var o = new float[96];
        long n = 0;
        double clock = 0;
        for (int t = 0; t < 48000 * 60; t += 96)
        {
            while (n <= clock)
            {
                var blk = new float[480];
                for (int i = 0; i < 480; i++) blk[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440f * (n + i) / 48000f);
                buf.Write(blk);
                n += 480;
            }
            clock += 96 * 1.0006;
            buf.Read(o);
            played.AddRange(o);
        }
        float maxStep = 0;
        for (int i = 48000 * 2; i + 1 < played.Count; i++) maxStep = MathF.Max(maxStep, MathF.Abs(played[i + 1] - played[i]));
        // A clean 440 Hz tone at 0.3 amplitude changes by at most 0.3 * 2π * 440 / 48000 ≈ 0.0173 per sample.
        Assert.True(maxStep < 0.06f, $"largest sample-to-sample jump {maxStep}");
    }
}
