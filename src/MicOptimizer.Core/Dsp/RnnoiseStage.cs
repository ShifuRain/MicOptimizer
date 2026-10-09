using RNNoise.NET;

namespace MicOptimizer.Core.Dsp;

/// <summary>
/// RNNoise neural noise suppression (fans, hiss, keyboard clicks). Works on fixed 10 ms frames at 48 kHz,
/// so it adds exactly one frame (480 samples = 10 ms) of latency.
///
/// RNNoise treats whispers as noise and removes them, so by default the stage is <see cref="Adaptive"/>:
/// the cleaned signal is only used while RNNoise itself hears normal or loud speech (voice probability
/// above <see cref="SpeechThreshold"/>). At other times the original (delayed) signal passes through for the
/// following gate to handle, which keeps whispers. With <see cref="Adaptive"/> off, <see cref="Strength"/>
/// is a fixed wet/dry mix.
/// </summary>
public sealed unsafe class RnnoiseStage : IAudioStage, IDisposable
{
    public const int FrameSize = 480;           // Native.FRAME_SIZE
    private const float Scale = 32767f;         // Native.SIGNAL_SCALE

    private readonly int _sr;
    private IntPtr _state;
    private readonly float[] _inFrame = new float[FrameSize];
    private readonly float[] _wetOut = new float[FrameSize];
    private float[] _dryIn = new float[FrameSize];
    private float[] _dryOut = new float[FrameSize];
    private int _pos;
    private int _holdLeft;
    private float _blend;

    public RnnoiseStage(int sampleRate)
    {
        if (sampleRate != 48000)
            throw new ArgumentException("RNNoise only supports 48 kHz audio.", nameof(sampleRate));
        _sr = sampleRate;
        _state = Native.rnnoise_create(IntPtr.Zero);
    }

    public bool Enabled { get; set; } = true;
    /// <summary>Use the cleaned signal only while speech is detected (keeps whispers).</summary>
    public bool Adaptive { get; set; } = true;
    /// <summary>Fixed mix when not adaptive: 0 = original (delayed), 1 = fully denoised.</summary>
    public float Strength { get; set; } = 1f;
    public float SpeechThreshold { get; set; } = 0.5f;
    /// <summary>How long the cleaned path stays active after speech is last detected (bridges pauses).</summary>
    public float HoldMs { get; set; } = 250f;
    public float FadeInMs { get; set; } = 5f;
    public float FadeOutMs { get; set; } = 80f;
    /// <summary>RNNoise's own estimate (0..1) that the last frame contained speech.</summary>
    public float VoiceProbability { get; private set; }
    /// <summary>Current share (0..1) of the cleaned signal in the output.</summary>
    public float Blend => Adaptive ? _blend : Math.Clamp(Strength, 0f, 1f);
    public int LatencySamples => FrameSize;

    public void Process(Span<float> buffer)
    {
        if (!Enabled || _state == IntPtr.Zero) return;

        float fixedWet = Math.Clamp(Strength, 0f, 1f);
        float fadeIn = Db.TimeCoeff(FadeInMs, _sr);
        float fadeOut = Db.TimeCoeff(FadeOutMs, _sr);

        for (int i = 0; i < buffer.Length; i++)
        {
            float wet;
            if (Adaptive)
            {
                float target = _holdLeft > 0 ? 1f : 0f;
                if (_holdLeft > 0) _holdLeft--;
                float c = target > _blend ? fadeIn : fadeOut;
                _blend = c * _blend + (1 - c) * target;
                wet = _blend;
            }
            else wet = fixedWet;

            float x = buffer[i];
            buffer[i] = _dryOut[_pos] * (1f - wet) + _wetOut[_pos] * wet;
            _inFrame[_pos] = x * Scale;
            _dryIn[_pos] = x;

            if (++_pos == FrameSize)
            {
                fixed (float* src = _inFrame)
                fixed (float* dst = _wetOut)
                    VoiceProbability = Native.rnnoise_process_frame(_state, dst, src);
                for (int k = 0; k < FrameSize; k++) _wetOut[k] /= Scale;
                if (VoiceProbability > SpeechThreshold)
                    _holdLeft = (int)(HoldMs * 0.001f * _sr);
                (_dryIn, _dryOut) = (_dryOut, _dryIn);
                _pos = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_state != IntPtr.Zero)
        {
            Native.rnnoise_destroy(_state);
            _state = IntPtr.Zero;
        }
    }
}
