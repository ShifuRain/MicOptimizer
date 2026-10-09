namespace MicOptimizer.Core.Dsp;

/// <summary>Single 2nd-order Butterworth section (high-pass or low-pass), used for sidechain filtering.</summary>
public sealed class Biquad
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = (float)b0; _b1 = (float)b1; _b2 = (float)b2; _a1 = (float)a1; _a2 = (float)a2;
    }

    public static Biquad HighPass(int sampleRate, float cutoffHz) => Make(sampleRate, cutoffHz, high: true);
    public static Biquad LowPass(int sampleRate, float cutoffHz) => Make(sampleRate, cutoffHz, high: false);

    /// <summary>Bell filter (RBJ cookbook): boosts or cuts around <paramref name="centerHz"/>.</summary>
    public static Biquad Peaking(int sampleRate, float centerHz, float gainDb, float q)
    {
        double A = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * centerHz / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double cos = Math.Cos(w0);
        double a0 = 1 + alpha / A;
        return new Biquad((1 + alpha * A) / a0, -2 * cos / a0, (1 - alpha * A) / a0, -2 * cos / a0, (1 - alpha / A) / a0);
    }

    /// <summary>High shelf (RBJ cookbook, slope 1): lifts or lowers everything above <paramref name="cornerHz"/>.</summary>
    public static Biquad HighShelf(int sampleRate, float cornerHz, float gainDb)
    {
        double A = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * cornerHz / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / 2 * Math.Sqrt((A + 1 / A) * (1 / 1.0 - 1) + 2);
        double sq = 2 * Math.Sqrt(A) * alpha;
        double a0 = (A + 1) - (A - 1) * cos + sq;
        return new Biquad(
            A * ((A + 1) + (A - 1) * cos + sq) / a0,
            -2 * A * ((A - 1) + (A + 1) * cos) / a0,
            A * ((A + 1) + (A - 1) * cos - sq) / a0,
            2 * ((A - 1) - (A + 1) * cos) / a0,
            ((A + 1) - (A - 1) * cos - sq) / a0);
    }

    private static Biquad Make(int sr, float f, bool high)
    {
        double w0 = 2 * Math.PI * f / sr;
        double alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        double cos = Math.Cos(w0);
        double a0 = 1 + alpha;
        double b0 = high ? (1 + cos) / 2 : (1 - cos) / 2;
        double b1 = high ? -(1 + cos) : (1 - cos);
        return new Biquad(b0 / a0, b1 / a0, b0 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    /// <summary>Takes over another filter's coefficients but keeps this one's memory, so tweaking a control doesn't click.</summary>
    public void CopyCoefficientsFrom(Biquad other)
    {
        _b0 = other._b0; _b1 = other._b1; _b2 = other._b2; _a1 = other._a1; _a2 = other._a2;
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}
