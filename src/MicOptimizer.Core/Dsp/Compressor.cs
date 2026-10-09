namespace MicOptimizer.Core.Dsp;

/// <summary>Feed-forward compressor with soft knee, evaluated in the log domain.</summary>
public sealed class Compressor : IAudioStage
{
    private readonly int _sr;
    private float _gainDb;

    public Compressor(int sampleRate) => _sr = sampleRate;

    public bool Enabled { get; set; } = true;
    public float ThresholdDb { get; set; } = -20f;
    public float Ratio { get; set; } = 3f;
    public float KneeDb { get; set; } = 6f;
    public float AttackMs { get; set; } = 8f;
    public float ReleaseMs { get; set; } = 150f;
    public float MakeupDb { get; set; } = 0f;

    /// <summary>Current gain reduction in dB (0 or negative), for the meter.</summary>
    public float GainReductionDb => _gainDb;

    public void Process(Span<float> buffer)
    {
        if (!Enabled) { _gainDb = 0f; return; }

        float attack = Db.TimeCoeff(AttackMs, _sr);
        float release = Db.TimeCoeff(ReleaseMs, _sr);
        float makeup = MakeupDb;

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float levelDb = Db.ToDb(MathF.Abs(x));

            float over = levelDb - ThresholdDb;
            float targetDb;
            if (2f * over < -KneeDb) targetDb = 0f;
            else if (2f * MathF.Abs(over) <= KneeDb)
            {
                float t = over + KneeDb / 2f;
                targetDb = (1f / Ratio - 1f) * t * t / (2f * KneeDb);
            }
            else targetDb = (1f / Ratio - 1f) * over;

            // Smooth the gain (attack when reducing more, release when recovering).
            float c = targetDb < _gainDb ? attack : release;
            _gainDb = c * _gainDb + (1 - c) * targetDb;

            buffer[i] = x * Db.ToLinear(_gainDb + makeup);
        }
    }
}
