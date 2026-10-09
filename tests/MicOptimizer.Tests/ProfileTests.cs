using MicOptimizer.Core.Dsp;
using MicOptimizer.Core.Profiles;
using Xunit;

namespace MicOptimizer.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MicOptimizerTests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Saved_profile_round_trips_through_json()
    {
        var store = new ProfileStore(_dir);
        store.Save(new Profile { Name = "My Voice", GateOpenDb = -41.5f, CompressorRatio = 3.5f, DeEsserEnabled = false });

        var loaded = store.LoadAll().Single(p => p.Name == "My Voice");
        Assert.Equal(-41.5f, loaded.GateOpenDb);
        Assert.Equal(3.5f, loaded.CompressorRatio);
        Assert.False(loaded.DeEsserEnabled);
    }

    [Fact]
    public void Built_ins_are_always_listed_first_and_cannot_be_overwritten_or_deleted()
    {
        var store = new ProfileStore(_dir);
        var all = store.LoadAll();
        Assert.Equal(BuiltInProfiles.Podcast, all[0].Name);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Profile { Name = BuiltInProfiles.Podcast }));
        Assert.Throws<InvalidOperationException>(() => store.Delete(BuiltInProfiles.Whisper));
    }

    [Fact]
    public void Corrupt_profile_files_are_skipped_not_fatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json");
        var store = new ProfileStore(_dir);
        store.Save(new Profile { Name = "Good" });

        var names = store.LoadAll().Select(p => p.Name).ToList();
        Assert.Contains("Good", names);
        Assert.DoesNotContain("broken", names);
    }

    [Fact]
    public void Profile_names_cannot_escape_the_profile_folder()
    {
        var store = new ProfileStore(_dir);
        store.Save(new Profile { Name = @"..\..\evil" });
        Assert.All(Directory.GetFiles(_dir), f => Assert.Equal(_dir, Path.GetDirectoryName(f)));
    }

    [Fact]
    public void Applying_a_profile_changes_the_chain_settings()
    {
        using var chain = new ProcessingChain(48000);
        var p = new Profile
        {
            InputGainDb = 6f, GateOpenDb = -40f, GateCloseDb = -48f, CompressorRatio = 4f,
            LevelerMaxBoostDb = 9f, EqPresenceDb = 3f, DeEsserEnabled = false, NoiseSuppression = false,
        };
        p.ApplyTo(chain);

        Assert.Equal(Db.ToLinear(6f), chain.InputGain, 4);
        Assert.Equal(-40f, chain.Gate.OpenThresholdDb);
        Assert.Equal(4f, chain.Compressor.Ratio);
        Assert.Equal(9f, chain.Leveler.MaxBoostDb);
        Assert.Equal(3f, chain.Eq.PresenceDb);
        Assert.False(chain.DeEsser.Enabled);
        Assert.False(chain.Rnnoise!.Enabled);
    }

    [Fact]
    public void Gate_close_threshold_is_never_set_above_the_open_threshold()
    {
        using var chain = new ProcessingChain(48000);
        new Profile { GateOpenDb = -50f, GateCloseDb = -40f }.ApplyTo(chain);
        Assert.True(chain.Gate.CloseThresholdDb <= chain.Gate.OpenThresholdDb);
    }

    [Fact]
    public void Default_profile_matches_the_chains_own_defaults()
    {
        // Guards against the Profile defaults drifting away from the tuned chain defaults.
        using var tuned = new ProcessingChain(48000);
        using var applied = new ProcessingChain(48000);
        new Profile().ApplyTo(applied);

        Assert.Equal(tuned.Gate.OpenThresholdDb, applied.Gate.OpenThresholdDb);
        Assert.Equal(tuned.Gate.CloseThresholdDb, applied.Gate.CloseThresholdDb);
        Assert.Equal(tuned.Gate.HoldMs, applied.Gate.HoldMs);
        Assert.Equal(tuned.Leveler.TargetDb, applied.Leveler.TargetDb);
        Assert.Equal(tuned.Leveler.MaxBoostDb, applied.Leveler.MaxBoostDb);
        Assert.Equal(tuned.Leveler.MaxCutDb, applied.Leveler.MaxCutDb);
        Assert.Equal(tuned.Compressor.ThresholdDb, applied.Compressor.ThresholdDb);
        Assert.Equal(tuned.Compressor.Ratio, applied.Compressor.Ratio);
        Assert.Equal(tuned.Compressor.MakeupDb, applied.Compressor.MakeupDb);
        Assert.Equal(tuned.Eq.MudDb, applied.Eq.MudDb);
        Assert.Equal(tuned.HighPass.CutoffHz, applied.HighPass.CutoffHz);
    }
}
