namespace MicOptimizer.Core.Dsp;

/// <summary>
/// Slow automatic gain control. Measures short-term speech level and moves the gain toward a
/// target over seconds, so whispers and normal speech end up at a similar loudness. Gain is
/// frozen while the signal is below <see cref="FreezeBelowDb"/> so pauses never pump up noise.
/// </summary>
public sealed class AutoLeveler : IAudioStage
{
    private readonly int _sr;
    private float _levelDb = -80f;
    private float _gainDb;

    public AutoLeveler(int sampleRate) => _sr = sampleRate;

    public bool Enabled { get; set; } = true;
    public float TargetDb { get; set; } = -24f;
    // Small range on purpose: whispers stay softer than speech and shouts stay louder (natural dynamics).
    public float MaxBoostDb { get; set; } = 6f;
    public float MaxCutDb { get; set; } = -3f;
    /// <summary>Below this measured level the gain holds still (treated as silence/noise).</summary>
    public float FreezeBelowDb { get; set; } = -55f;
    /// <summary>How fast the gain rises when the voice is too quiet.</summary>
    public float BoostTimeMs { get; set; } = 1500f;
    /// <summary>How fast the gain drops when the voice is too loud.</summary>
    public float CutTimeMs { get; set; } = 250f;

    public float CurrentGainDb => _gainDb;

    public void Process(Span<float> buffer)
    {
        if (!Enabled) return;

        float levelAttack = Db.TimeCoeff(30f, _sr);
        float levelRelease = Db.TimeCoeff(400f, _sr);
        float boost = Db.TimeCoeff(BoostTimeMs, _sr);
        float cut = Db.TimeCoeff(CutTimeMs, _sr);

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float inDb = Db.ToDb(MathF.Abs(x));
            float lc = inDb > _levelDb ? levelAttack : levelRelease;
            _levelDb = lc * _levelDb + (1 - lc) * inDb;

            if (_levelDb > FreezeBelowDb)
            {
                float wanted = Math.Clamp(TargetDb - _levelDb, MaxCutDb, MaxBoostDb);
                float c = wanted < _gainDb ? cut : boost;
                _gainDb = c * _gainDb + (1 - c) * wanted;
            }

            buffer[i] = x * Db.ToLinear(_gainDb);
        }
    }
}
