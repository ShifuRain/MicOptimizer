namespace MicOptimizer.Core.Profiles;

/// <summary>
/// Factory presets. "Podcast" is the tuned default from the calibration of the Blue Spark + Scarlett Solo setup;
/// the others are reasoned starting points that have not been listened to yet.
/// </summary>
public static class BuiltInProfiles
{
    public const string Podcast = "Podcast";
    public const string Whisper = "Whisper / ASMR";
    public const string Loud = "Loud / Gaming";
    public const string Flat = "Flat (no processing)";

    public static IReadOnlyList<Profile> All() => new[]
    {
        new Profile { Name = Podcast, Description = "Balanced and natural: steady level, keyboard and fan noise removed." },

        new Profile
        {
            Name = Whisper,
            Description = "Keeps very quiet speech: no AI noise removal, a more open gate, and more lift for soft voices.",
            NoiseSuppression = false,
            GateOpenDb = -50f, GateCloseDb = -58f, GateHoldMs = 150f, GateReductionDb = -20f,
            LevelerMaxBoostDb = 12f, LevelerMaxCutDb = -3f, LevelerTargetDb = -24f,
            CompressorThresholdDb = -22f, CompressorRatio = 1.8f,
            DeEsserEnabled = false,
        },

        new Profile
        {
            Name = Loud,
            Description = "For shouting and loud dynamic voices: tighter gate, stronger compression, loud peaks pulled in.",
            GateOpenDb = -42f, GateCloseDb = -50f, GateHoldMs = 80f,
            LevelerMaxBoostDb = 6f, LevelerMaxCutDb = -8f, LevelerTargetDb = -22f,
            CompressorThresholdDb = -20f, CompressorRatio = 4f, CompressorMakeupDb = 3f,
            LimiterCeilingDb = -2f,
        },

        new Profile
        {
            Name = Flat,
            Description = "Only a safety limiter. Use to compare against the processed sound.",
            NoiseSuppression = false, GateEnabled = false, EqEnabled = false, LevelerEnabled = false,
            CompressorEnabled = false, DeEsserEnabled = false, HighPassHz = 60f,
        },
    };
}
