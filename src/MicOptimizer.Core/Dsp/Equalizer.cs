namespace MicOptimizer.Core.Dsp;

/// <summary>Three-band voice EQ: mud cut, presence boost and an "air" shelf. Defaults are deliberately mild.</summary>
public sealed class Equalizer : IAudioStage
{
    private readonly int _sr;
    private readonly Biquad _mud, _presence, _air;
    private float _mudDb = -2f, _presenceDb = 1.5f, _airDb = 1f;

    public Equalizer(int sampleRate)
    {
        _sr = sampleRate;
        _mud = MakeMud();
        _presence = MakePresence();
        _air = MakeAir();
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Cut around 250 Hz that removes boxiness/proximity "mud" (negative = cut).</summary>
    public float MudDb
    {
        get => _mudDb;
        set { if (value == _mudDb) return; _mudDb = value; _mud.CopyCoefficientsFrom(MakeMud()); }
    }

    /// <summary>Boost around 3.5 kHz for clarity.</summary>
    public float PresenceDb
    {
        get => _presenceDb;
        set { if (value == _presenceDb) return; _presenceDb = value; _presence.CopyCoefficientsFrom(MakePresence()); }
    }

    /// <summary>High shelf above 10 kHz for sparkle.</summary>
    public float AirDb
    {
        get => _airDb;
        set { if (value == _airDb) return; _airDb = value; _air.CopyCoefficientsFrom(MakeAir()); }
    }

    private Biquad MakeMud() => Biquad.Peaking(_sr, 250f, _mudDb, 1.0f);
    private Biquad MakePresence() => Biquad.Peaking(_sr, 3500f, _presenceDb, 0.8f);
    private Biquad MakeAir() => Biquad.HighShelf(_sr, 10000f, _airDb);

    public void Process(Span<float> buffer)
    {
        if (!Enabled) return;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = _air.Process(_presence.Process(_mud.Process(buffer[i])));
    }
}
