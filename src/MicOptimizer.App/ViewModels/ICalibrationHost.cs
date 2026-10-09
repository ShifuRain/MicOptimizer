using MicOptimizer.Audio;
using MicOptimizer.Core.Calibration;
using MicOptimizer.Core.Profiles;

namespace MicOptimizer.App.ViewModels;

/// <summary>
/// What the calibration wizard needs from the app: the raw microphone, a way to silence the output while it listens,
/// the settings being edited and somewhere to put the result. <see cref="MainViewModel"/> is the real one.
/// </summary>
public interface ICalibrationHost
{
    bool IsRunning { get; }
    int EngineSampleRate { get; }

    /// <summary>Raised about 30 times a second with (input dB, output dB, gate open); the wizard uses the input level.</summary>
    event Action<float, float, bool>? Tick;

    Profile WorkingProfileSnapshot();

    /// <summary>Listens to the raw microphone, optionally silencing the output. Pass null to stop.</summary>
    void SetCalibrationCapture(AudioEngine.InputTapHandler? tap, bool muteOutput);

    void ApplyCalibration(CalibrationResult result);
}
