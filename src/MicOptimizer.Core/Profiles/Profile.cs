using MicOptimizer.Core.Dsp;

namespace MicOptimizer.Core.Profiles;

/// <summary>A complete set of processing settings that can be applied to a <see cref="ProcessingChain"/> and saved as JSON.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Untitled";
    public string Description { get; set; } = "";

    public float InputGainDb { get; set; }
    public float HighPassHz { get; set; } = 90f;

    public bool NoiseSuppression { get; set; } = true;
    /// <summary>Use the cleaned signal only while normal/loud speech is detected (keeps whispers).</summary>
    public bool NoiseAdaptive { get; set; } = true;
    /// <summary>Fixed strength used when <see cref="NoiseAdaptive"/> is off.</summary>
    public float NoiseStrength { get; set; } = 1f;

    public bool GateEnabled { get; set; } = true;
    public float GateOpenDb { get; set; } = -46f;
    public float GateCloseDb { get; set; } = -54f;
    public float GateHoldMs { get; set; } = 100f;
    public float GateReductionDb { get; set; } = -30f;

    public bool EqEnabled { get; set; } = true;
    public float EqMudDb { get; set; } = -2f;
    public float EqPresenceDb { get; set; } = 1.5f;
    public float EqAirDb { get; set; } = 1f;

    public bool LevelerEnabled { get; set; } = true;
    public float LevelerTargetDb { get; set; } = -24f;
    public float LevelerMaxBoostDb { get; set; } = 6f;
    public float LevelerMaxCutDb { get; set; } = -3f;

    public bool CompressorEnabled { get; set; } = true;
    public float CompressorThresholdDb { get; set; } = -16f;
    public float CompressorRatio { get; set; } = 2f;
    public float CompressorMakeupDb { get; set; } = 2f;

    public bool DeEsserEnabled { get; set; } = true;
    public float DeEsserThresholdDb { get; set; } = -8f;
    public float DeEsserMaxReductionDb { get; set; } = 6f;

    public bool LimiterEnabled { get; set; } = true;
    public float LimiterCeilingDb { get; set; } = -1f;

    public Profile Clone() => (Profile)MemberwiseClone();

    public override string ToString() => Name;

    public void ApplyTo(ProcessingChain chain)
    {
        chain.InputGain = Db.ToLinear(InputGainDb);
        chain.HighPass.CutoffHz = HighPassHz;

        if (chain.Rnnoise is { } rn)
        {
            rn.Enabled = NoiseSuppression;
            rn.Adaptive = NoiseAdaptive;
            rn.Strength = NoiseStrength;
        }

        chain.Gate.Enabled = GateEnabled;
        chain.Gate.OpenThresholdDb = GateOpenDb;
        chain.Gate.CloseThresholdDb = Math.Min(GateCloseDb, GateOpenDb);
        chain.Gate.HoldMs = GateHoldMs;
        chain.Gate.ReductionDb = GateReductionDb;

        chain.Eq.Enabled = EqEnabled;
        chain.Eq.MudDb = EqMudDb;
        chain.Eq.PresenceDb = EqPresenceDb;
        chain.Eq.AirDb = EqAirDb;

        chain.Leveler.Enabled = LevelerEnabled;
        chain.Leveler.TargetDb = LevelerTargetDb;
        chain.Leveler.MaxBoostDb = LevelerMaxBoostDb;
        chain.Leveler.MaxCutDb = LevelerMaxCutDb;

        chain.Compressor.Enabled = CompressorEnabled;
        chain.Compressor.ThresholdDb = CompressorThresholdDb;
        chain.Compressor.Ratio = Math.Max(1f, CompressorRatio);
        chain.Compressor.MakeupDb = CompressorMakeupDb;

        chain.DeEsser.Enabled = DeEsserEnabled;
        chain.DeEsser.ThresholdDb = DeEsserThresholdDb;
        chain.DeEsser.MaxReductionDb = DeEsserMaxReductionDb;

        chain.Limiter.Enabled = LimiterEnabled;
        chain.Limiter.CeilingDb = LimiterCeilingDb;
    }
}
