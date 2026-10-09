using System.Text.Json;

namespace MicOptimizer.Core.Profiles;

/// <summary>Built-in presets plus the user's own profiles, one JSON file per profile.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicOptimizer", "profiles");
    }

    public string Directory => _directory;

    public static bool IsBuiltIn(string name) =>
        BuiltInProfiles.All().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Built-ins first, then user profiles. A user file with a built-in's name is ignored.</summary>
    public IReadOnlyList<Profile> LoadAll()
    {
        var list = BuiltInProfiles.All().ToList();
        if (!System.IO.Directory.Exists(_directory)) return list;

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file));
                if (p is not null && !string.IsNullOrWhiteSpace(p.Name) && !IsBuiltIn(p.Name))
                    list.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A corrupt or unreadable profile file must never stop the app from starting; skip it.
            }
        }
        return list;
    }

    public void Save(Profile profile)
    {
        if (IsBuiltIn(profile.Name))
            throw new InvalidOperationException($"'{profile.Name}' is a built-in profile; save it under a different name.");
        System.IO.Directory.CreateDirectory(_directory);
        File.WriteAllText(PathFor(profile.Name), JsonSerializer.Serialize(profile, Json));
    }

    public void Delete(string name)
    {
        if (IsBuiltIn(name)) throw new InvalidOperationException("Built-in profiles cannot be deleted.");
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name)
    {
        // Keep file names safe: profile names come from the user.
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) throw new ArgumentException("Profile name is empty.", nameof(name));
        return Path.Combine(_directory, safe + ".json");
    }
}
