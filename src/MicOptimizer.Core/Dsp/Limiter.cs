namespace MicOptimizer.Core.Dsp;

/// <summary>Lookahead brickwall limiter so shouting never clips.</summary>
public sealed class Limiter : IAudioStage
{
    private readonly int _sr;
    private readonly float[] _delay;
    private readonly float[] _need;
    private int _pos;
    private float _gain = 1f;

    public Limiter(int sampleRate, float lookaheadMs = 1f)
    {
        _sr = sampleRate;
        _delay = new float[Math.Max(1, (int)(lookaheadMs * 0.001f * sampleRate))];
        _need = new float[_delay.Length];
        Array.Fill(_need, 1f);
    }

    public bool Enabled { get; set; } = true;
    public float CeilingDb { get; set; } = -1f;
    public float ReleaseMs { get; set; } = 80f;
    public int LatencySamples => _delay.Length;

    public void Process(Span<float> buffer)
    {
        if (!Enabled) return;
        float ceiling = Db.ToLinear(CeilingDb);
        float release = Db.TimeCoeff(ReleaseMs, _sr);

        for (int i = 0; i < buffer.Length; i++)
        {
            float incoming = buffer[i];
            float needed = MathF.Abs(incoming) > ceiling ? ceiling / MathF.Abs(incoming) : 1f;

            // The gain must satisfy every sample still in the lookahead window (including the one
            // about to be output), so take the minimum; release smoothly afterwards to avoid pumping.
            float target = needed;
            for (int k = 0; k < _need.Length; k++) if (_need[k] < target) target = _need[k];
            _gain = target < _gain ? target : release * _gain + (1 - release) * target;

            float outSample = _delay[_pos] * _gain;
            _delay[_pos] = incoming;
            _need[_pos] = needed;
            _pos = (_pos + 1) % _delay.Length;
            buffer[i] = outSample;
        }
    }
}
