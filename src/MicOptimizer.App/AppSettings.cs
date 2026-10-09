using System.IO;
using System.Text.Json;

namespace MicOptimizer.App;

/// <summary>Remembers the devices and profile between launches (%APPDATA%\MicOptimizer\settings.json).</summary>
public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    /// <summary>0-based channel on the audio interface that carries the microphone.</summary>
    public int InputChannel { get; set; }
    public string? LastProfile { get; set; }
    /// <summary>Start processing as soon as the app opens (when both devices are still present).</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>"Lowest" (ASIO input, falls back automatically) or "Standard" (Windows low-latency audio).</summary>
    public string LatencyMode { get; set; } = "Lowest";
    /// <summary>True once the "still running in the tray" balloon has been shown, so it only appears the first time.</summary>
    public bool TrayHintShown { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicOptimizer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable settings are not worth failing over; fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a remembered device is harmless; never crash because of it.
        }
    }
}
