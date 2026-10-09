namespace MicOptimizer.Core.Dsp;

/// <summary>
/// Split-band de-esser. Splits off everything above <c>crossoverHz</c>, and turns that band down when it
/// dominates the full signal (a sibilant "s"/"t"). Whispers are naturally high-frequency heavy, so the
/// effect is scaled by <see cref="Strength"/> (the chain feeds it the speech blend from RNNoise) and does
/// nothing while whispering.
/// </summary>
public sealed class DeEsser : IAudioStage
{
    private readonly int _sr;
    private readonly Biquad _hp1, _hp2, _lp1, _lp2;
    private float _fullEnv, _hfEnv, _gainDb;

    public DeEsser(int sampleRate, float crossoverHz = 5000f)
    {
        _sr = sampleRate;
        // Linkwitz-Riley 4th-order crossover (two cascaded Butterworth sections per side): the low and
        // high bands sum back to a flat magnitude, so the split is transparent when no reduction is applied.
        _hp1 = Biquad.HighPass(sampleRate, crossoverHz);
        _hp2 = Biquad.HighPass(sampleRate, crossoverHz);
        _lp1 = Biquad.LowPass(sampleRate, crossoverHz);
        _lp2 = Biquad.LowPass(sampleRate, crossoverHz);
    }

    public bool Enabled { get; set; } = true;
    /// <summary>0..1 scale of the effect; the chain sets this from the speech/whisper detector.</summary>
    public float Strength { get; set; } = 1f;
    /// <summary>High band relative to full band (dB) above which reduction starts.</summary>
    public float ThresholdDb { get; set; } = -8f;
    public float Ratio { get; set; } = 4f;
    public float MaxReductionDb { get; set; } = 6f;
    public float ReductionDb => -_gainDb;

    public void Process(Span<float> buffer)
    {
        if (!Enabled) { _gainDb = 0f; return; }

        float envAttack = Db.TimeCoeff(1f, _sr), envRelease = Db.TimeCoeff(20f, _sr);
        float gAttack = Db.TimeCoeff(1f, _sr), gRelease = Db.TimeCoeff(30f, _sr);
        float strength = Math.Clamp(Strength, 0f, 1f);

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float hf = _hp2.Process(_hp1.Process(x));

            float ax = MathF.Abs(x), ah = MathF.Abs(hf);
            _fullEnv = (ax > _fullEnv ? envAttack : envRelease) * _fullEnv + (1 - (ax > _fullEnv ? envAttack : envRelease)) * ax;
            _hfEnv = (ah > _hfEnv ? envAttack : envRelease) * _hfEnv + (1 - (ah > _hfEnv ? envAttack : envRelease)) * ah;

            float targetDb = 0f;
            // Ignore near-silence so noise floor ratios can't trigger the effect.
            if (_fullEnv > 1e-4f)
            {
                float over = Db.ToDb(_hfEnv) - Db.ToDb(_fullEnv) - ThresholdDb;
                if (over > 0f)
                    targetDb = -MathF.Min(MaxReductionDb, over * (1f - 1f / Ratio)) * strength;
            }

            float c = targetDb < _gainDb ? gAttack : gRelease;
            _gainDb = c * _gainDb + (1 - c) * targetDb;

            float low = _lp2.Process(_lp1.Process(x));
            buffer[i] = low + hf * Db.ToLinear(_gainDb);
        }
    }
}
