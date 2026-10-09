namespace MicOptimizer.Core.Dsp;

/// <summary>2nd-order Butterworth high-pass (removes rumble and desk thumps).</summary>
public sealed class HighPassFilter : IAudioStage
{
    private readonly int _sampleRate;
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;
    private float _cutoffHz;

    public HighPassFilter(int sampleRate, float cutoffHz = 90f)
    {
        _sampleRate = sampleRate;
        CutoffHz = cutoffHz;
    }

    public bool Enabled { get; set; } = true;

    public float CutoffHz
    {
        get => _cutoffHz;
        set
        {
            _cutoffHz = value;
            double w0 = 2 * Math.PI * value / _sampleRate;
            double alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
            double cos = Math.Cos(w0);
            double a0 = 1 + alpha;
            _b0 = (float)((1 + cos) / 2 / a0);
            _b1 = (float)(-(1 + cos) / a0);
            _b2 = _b0;
            _a1 = (float)(-2 * cos / a0);
            _a2 = (float)((1 - alpha) / a0);
        }
    }

    public void Process(Span<float> buffer)
    {
        if (!Enabled) return;
        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            buffer[i] = y;
        }
    }
}
