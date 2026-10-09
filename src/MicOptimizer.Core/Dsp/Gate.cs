namespace MicOptimizer.Core.Dsp;

/// <summary>
/// Downward expander with hysteresis and hold. Below the close threshold the signal is
/// attenuated by <see cref="ReductionDb"/> (not fully muted, so it sounds natural).
/// The open/close decision listens to a band-limited copy of the signal (sidechain) so
/// low-frequency desk thumps from typing don't open the gate; the audio itself stays full-band.
/// </summary>
public sealed class Gate : IAudioStage
{
    private readonly int _sr;
    private readonly Biquad _hp1, _hp2, _lp;
    private float _env, _gain = 1f;
    private int _holdLeft;

    public Gate(int sampleRate, float sidechainLowHz = 150f, float sidechainHighHz = 5000f)
    {
        _sr = sampleRate;
        // Two cascaded high-pass sections (4th order) give a steep cut on the thump region.
        _hp1 = Biquad.HighPass(sampleRate, sidechainLowHz);
        _hp2 = Biquad.HighPass(sampleRate, sidechainLowHz);
        _lp = Biquad.LowPass(sampleRate, sidechainHighHz);
    }

    public bool Enabled { get; set; } = true;
    /// <summary>Sidechain level above which the gate opens.</summary>
    public float OpenThresholdDb { get; set; } = -46f;
    /// <summary>Sidechain level below which the gate closes (lower than open = hysteresis).</summary>
    public float CloseThresholdDb { get; set; } = -54f;
    public float ReductionDb { get; set; } = -30f;
    public float HoldMs { get; set; } = 100f;
    public float AttackMs { get; set; } = 2f;
    public float ReleaseMs { get; set; } = 120f;

    public bool IsOpen { get; private set; }
    /// <summary>Current sidechain envelope in dBFS, for the meter and tuning.</summary>
    public float SidechainDb => Db.ToDb(_env);

    public void Process(Span<float> buffer)
    {
        if (!Enabled) { IsOpen = true; return; }

        float attack = Db.TimeCoeff(AttackMs, _sr);
        float release = Db.TimeCoeff(ReleaseMs, _sr);
        float envAttack = Db.TimeCoeff(1f, _sr);
        float envRelease = Db.TimeCoeff(30f, _sr);
        float open = Db.ToLinear(OpenThresholdDb);
        float close = Db.ToLinear(CloseThresholdDb);
        float floor = Db.ToLinear(ReductionDb);
        int hold = (int)(HoldMs * 0.001f * _sr);

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float a = MathF.Abs(_lp.Process(_hp2.Process(_hp1.Process(x))));
            float c = a > _env ? envAttack : envRelease;
            _env = c * _env + (1 - c) * a;

            if (_env > open) { IsOpen = true; _holdLeft = hold; }
            else if (_env < close)
            {
                if (_holdLeft > 0) _holdLeft--;
                else IsOpen = false;
            }

            float target = IsOpen ? 1f : floor;
            float k = target > _gain ? attack : release;
            _gain = k * _gain + (1 - k) * target;
            buffer[i] = x * _gain;
        }
    }
}
