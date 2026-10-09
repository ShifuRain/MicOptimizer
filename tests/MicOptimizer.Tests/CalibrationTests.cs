using MicOptimizer.Core.Calibration;
using MicOptimizer.Core.Dsp;
using MicOptimizer.Core.Profiles;
using Xunit;

namespace MicOptimizer.Tests;

/// <summary>Calibration is exercised with synthetic rooms, voices and keyboards, never with anyone's real recordings.</summary>
public class CalibrationTests
{
    private const int Sr = 48000;

    // ---- synthetic sounds ----

    private static float[] Silence(double seconds) => Room(new float[(int)(seconds * Sr)], -62, 1);

    /// <summary>Steady room noise at the given RMS level, added to whatever is already in the buffer.</summary>
    private static float[] Room(float[] signal, double rmsDb, int seed)
    {
        var rng = new Random(seed);
        double amp = Db.ToLinear((float)rmsDb) * Math.Sqrt(3);          // uniform noise: RMS = amp / sqrt(3)
        for (int i = 0; i < signal.Length; i++) signal[i] += (float)((rng.NextDouble() * 2 - 1) * amp);
        return signal;
    }

    /// <summary>Spoken-looking audio: harmonics of 140 Hz in words 350 ms long separated by 150 ms pauses.</summary>
    private static float[] Voice(double seconds, double rmsDb, int seed = 2)
    {
        var s = new float[(int)(seconds * Sr)];
        var raw = new double[s.Length];
        double sumSq = 0; int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            bool word = i % (Sr / 2) < Sr * 350 / 1000;
            if (!word) continue;
            double v = 0;
            for (int h = 1; h <= 20; h++) v += Math.Sin(2 * Math.PI * 140 * h * i / Sr) / h;
            raw[i] = v; sumSq += v * v; count++;
        }
        double gain = Db.ToLinear((float)rmsDb) / Math.Sqrt(sumSq / count);
        for (int i = 0; i < s.Length; i++) s[i] = (float)(raw[i] * gain);
        return Room(s, -62, seed);
    }

    /// <summary>A whisper: hiss between 1 and 5 kHz in short phrases, with no voiced low frequencies.</summary>
    private static float[] Whisper(double seconds, double rmsDb)
    {
        var s = new float[(int)(seconds * Sr)];
        var rng = new Random(3);
        var hp = Biquad.HighPass(Sr, 1000); var lp = Biquad.LowPass(Sr, 5000);
        double sumSq = 0; int count = 0;
        var raw = new double[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double v = lp.Process(hp.Process((float)(rng.NextDouble() * 2 - 1)));
            if (i % (Sr / 2) < Sr * 300 / 1000) { raw[i] = v; sumSq += v * v; count++; }
        }
        double gain = Db.ToLinear((float)rmsDb) / Math.Sqrt(sumSq / count);
        for (int i = 0; i < s.Length; i++) s[i] = (float)(raw[i] * gain);
        return Room(s, -62, 4);
    }

    /// <summary>Typing: loud low-frequency desk thumps with quieter key clicks.</summary>
    private static float[] Typing(double seconds)
    {
        var s = new float[(int)(seconds * Sr)];
        var rng = new Random(5);
        for (int start = 0; start < s.Length - 2400; start += Sr * 180 / 1000)
        {
            for (int i = 0; i < Sr * 25 / 1000; i++)                     // 25 ms thump at 60 Hz, -22 dBFS
                s[start + i] += 0.08f * MathF.Sin(2 * MathF.PI * 60 * i / Sr) * (1 - i / (Sr * 0.025f));
            for (int i = 0; i < Sr * 4 / 1000; i++)                      // 4 ms click, about -55 dBFS
                s[start + i] += (float)((rng.NextDouble() * 2 - 1) * 0.0018);
        }
        return Room(s, -62, 6);
    }

    private static CalibrationRecording Recording(params (CalibrationPhase phase, float[] audio)[] phases)
    {
        var r = new CalibrationRecording(Sr);
        foreach (var (phase, audio) in phases) r.Phases[phase] = audio;
        return r;
    }

    /// <summary>Noise removal is switched off so the synthetic sounds are judged by the gate alone.</summary>
    private static Profile Plain() => new() { NoiseSuppression = false };

    // ---- tests ----

    [Fact]
    public void Room_and_speech_alone_give_a_threshold_just_above_the_room_noise()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Voice(8, -26))), Plain());

        Assert.Null(analysis.Error);
        var result = analysis.Choose(CalibrationBias.Balanced);
        Assert.True(result.Chosen.SilenceOpenPercent <= 2, "the gate must stay shut in the quiet room");
        Assert.True(result.Chosen.SpeechKeptPercent >= 97, $"speech kept {result.Chosen.SpeechKeptPercent:0}%");
        Assert.InRange(result.GateOpenDb, -66f, -40f);
        Assert.True(result.GateCloseDb < result.GateOpenDb, "closing must happen below opening (hysteresis)");
        Assert.Equal(3, result.Advice.Count(a => a.Text.Contains("skipped")));          // typing, whisper and shouting
        Assert.Contains(result.Advice, a => a.Text.StartsWith("No problems found"));    // and nothing to fix
    }

    [Fact]
    public void Typing_thumps_stay_out_while_whispers_and_speech_come_through()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)),
            (CalibrationPhase.Typing, Typing(6)),
            (CalibrationPhase.Whisper, Whisper(6, -46)),
            (CalibrationPhase.Speech, Voice(8, -26)),
            (CalibrationPhase.Shout, Voice(5, -14, seed: 7))), Plain());

        var r = analysis.Choose(CalibrationBias.Balanced).Chosen;
        Assert.True(r.SilenceOpenPercent <= 2, $"silence open {r.SilenceOpenPercent:0}%");
        Assert.True(r.TypingOpenPercent <= 15, $"typing opened the gate {r.TypingOpenPercent:0}% of the time");
        Assert.True(r.WhisperKeptPercent >= 75, $"whisper kept {r.WhisperKeptPercent:0}%");
        Assert.True(r.SpeechKeptPercent >= 97 && r.ShoutKeptPercent >= 97);
    }

    [Fact]
    public void The_three_biases_order_the_threshold_from_sensitive_to_strict()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Voice(8, -26))), Plain());

        float keep = analysis.Choose(CalibrationBias.KeepQuietSpeech).GateOpenDb;
        float balanced = analysis.Choose(CalibrationBias.Balanced).GateOpenDb;
        float block = analysis.Choose(CalibrationBias.BlockMoreNoise).GateOpenDb;
        Assert.True(keep <= balanced && balanced <= block && keep < block, $"{keep}, {balanced}, {block}");
    }

    [Fact]
    public void Clipping_is_reported_with_how_far_to_turn_the_gain_down()
    {
        var loud = Voice(8, -8);
        for (int i = 0; i < 100; i++) loud[1000 + i] = 0.99f;     // a peak right at the top
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, loud)), Plain());

        var advice = analysis.Choose(CalibrationBias.Balanced).Advice;
        Assert.Contains(advice, a => a.Severity == AdviceSeverity.Warning && a.Text.Contains("clip") && a.Text.Contains("down"));
    }

    [Fact]
    public void A_very_quiet_voice_is_reported_with_how_far_to_turn_the_gain_up()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Voice(8, -46))), Plain());

        var advice = analysis.Choose(CalibrationBias.Balanced).Advice;
        Assert.Contains(advice, a => a.Severity == AdviceSeverity.Warning && a.Text.Contains("quiet") && a.Text.Contains("up"));
    }

    [Fact]
    public void A_voice_barely_above_the_room_noise_suggests_moving_the_mic_closer()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Room(new float[5 * Sr], -48, 11)), (CalibrationPhase.Speech, Voice(8, -34))), Plain());

        Assert.Contains(analysis.Choose(CalibrationBias.Balanced).Advice, a => a.Text.Contains("closer"));
    }

    [Fact]
    public void Recordings_with_no_voice_are_rejected_with_a_helpful_message()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Room(new float[8 * Sr], -85, 9))), Plain());

        Assert.NotNull(analysis.Error);
        Assert.Contains("Find my mic", analysis.Error);
        Assert.Throws<InvalidOperationException>(() => analysis.Choose(CalibrationBias.Balanced));
    }

    [Fact]
    public void The_quiet_room_and_speech_recordings_are_both_required()
    {
        Assert.NotNull(GateCalibrator.Analyze(Recording((CalibrationPhase.Speech, Voice(8, -26))), Plain()).Error);
        Assert.NotNull(GateCalibrator.Analyze(Recording((CalibrationPhase.Silence, Silence(5))), Plain()).Error);
    }

    [Fact]
    public void Applying_a_result_changes_only_the_gate_settings()
    {
        var result = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Voice(8, -26))), Plain())
            .Choose(CalibrationBias.Balanced);

        var profile = new Profile { Name = "Mine", CompressorRatio = 3.3f, EqPresenceDb = 4f, GateEnabled = false };
        result.ApplyTo(profile);

        Assert.True(profile.GateEnabled);
        Assert.Equal(result.GateOpenDb, profile.GateOpenDb);
        Assert.Equal(result.GateCloseDb, profile.GateCloseDb);
        Assert.Equal(result.GateHoldMs, profile.GateHoldMs);
        Assert.Equal("Mine", profile.Name);
        Assert.Equal(3.3f, profile.CompressorRatio);
        Assert.Equal(4f, profile.EqPresenceDb);
    }

    [Fact]
    public void The_profiles_input_gain_is_taken_into_account()
    {
        // The same recordings with +12 dB of input gain look 12 dB louder to the gate, so its threshold moves up.
        var recording = Recording((CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Voice(8, -40)));
        float plain = GateCalibrator.Analyze(recording, Plain()).Choose(CalibrationBias.Balanced).GateOpenDb;
        var boosted = Plain(); boosted.InputGainDb = 12;
        float withGain = GateCalibrator.Analyze(recording, boosted).Choose(CalibrationBias.Balanced).GateOpenDb;
        Assert.True(withGain > plain, $"{withGain} should be above {plain}");
    }

    [Fact]
    public void Works_with_noise_removal_switched_on()
    {
        var analysis = GateCalibrator.Analyze(Recording(
            (CalibrationPhase.Silence, Silence(5)), (CalibrationPhase.Speech, Voice(8, -26))), new Profile());
        Assert.Null(analysis.Error);
        Assert.NotEmpty(analysis.Candidates);
        _ = analysis.Choose(CalibrationBias.Balanced);
    }
}
