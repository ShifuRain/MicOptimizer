using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using MicOptimizer.Core.Calibration;
using MicOptimizer.Core.Profiles;

namespace MicOptimizer.App.ViewModels;

public enum CalibrationState { Intro, GetReady, Recording, Analyzing, Result, Failed }

public sealed record CalibrationStep(CalibrationPhase Phase, string Title, string Instruction, double Seconds, bool Optional);

/// <summary>One small dot in the step bar.</summary>
public sealed class StepChip : ObservableObject
{
    private bool _current, _done, _skipped;
    public StepChip(string title) => Title = title;
    public string Title { get; }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
    public bool IsDone { get => _done; set => Set(ref _done, value); }
    public bool IsSkipped { get => _skipped; set => Set(ref _skipped, value); }
}

public sealed record ChangeLine(string Label, string Before, string After);
public sealed record AdviceLine(bool IsWarning, string Text);

/// <summary>
/// Drives the calibration wizard: listens to the raw microphone for each step while the output is silenced,
/// analyses the recordings off the UI thread, and offers the result for the profile being edited.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject, IDisposable
{
    private const double GetReadySeconds = 3;

    private static readonly CalibrationStep[] Steps =
    {
        new(CalibrationPhase.Silence, "Quiet room", "Stay completely quiet and keep your hands off the keyboard and mouse.", 5, false),
        new(CalibrationPhase.Typing, "Typing and clicking", "Type the way you normally do and click the mouse. Don't speak.", 6, true),
        new(CalibrationPhase.Whisper, "Whisper", "Whisper a sentence, pause, then whisper again. Use the quietest voice you want people to hear.", 6, true),
        new(CalibrationPhase.Speech, "Normal voice", "Talk the way you would on a call or a podcast. Reading something aloud works well.", 8, false),
        new(CalibrationPhase.Shout, "Loud", "Shout a few times, as loud as you would ever get.", 5, true),
    };

    private readonly ICalibrationHost _main;
    private readonly Profile _baseProfile;
    private readonly CalibrationRecording _recording;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();

    private float[]? _buffer;
    private int _position;
    private int _stepIndex = -1;
    private CalibrationAnalysis? _analysis;
    private readonly Dictionary<CalibrationBias, CalibrationResult> _results = new();

    private CalibrationState _state = CalibrationState.Intro;
    private double _progress, _level = -70;
    private string _countdown = "", _error = "";
    private CalibrationBias _bias = CalibrationBias.Balanced;
    private bool _showBiasChoice;
    private CalibrationResult? _result;

    public CalibrationViewModel(ICalibrationHost main)
    {
        _main = main;
        _baseProfile = main.WorkingProfileSnapshot();
        _recording = new CalibrationRecording(main.EngineSampleRate);

        foreach (var s in Steps) Chips.Add(new StepChip(s.Title));

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => OnTimer();
        _main.Tick += OnMainTick;

        StartCommand = new RelayCommand(Begin);
        SkipCommand = new RelayCommand(Skip, () => CanSkip);
        ApplyCommand = new RelayCommand(Apply, () => State == CalibrationState.Result);
        RedoCommand = new RelayCommand(Redo);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
    }

    /// <summary>Raised when the window should close; true when settings were applied.</summary>
    public event Action<bool>? CloseRequested;

    public ObservableCollection<StepChip> Chips { get; } = new();
    public ObservableCollection<ChangeLine> Changes { get; } = new();
    public ObservableCollection<string> SummaryLines { get; } = new();
    public ObservableCollection<AdviceLine> AdviceLines { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CancelCommand { get; }

    // ---- what the window shows ----

    public CalibrationState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            foreach (var n in new[] { nameof(IsIntro), nameof(IsCapturing), nameof(IsGetReady), nameof(IsAnalyzing), nameof(IsResult), nameof(IsFailed), nameof(CanSkip), nameof(StepHeading) })
                Raise(n);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIntro => State == CalibrationState.Intro;
    public bool IsGetReady => State == CalibrationState.GetReady;
    public bool IsCapturing => State is CalibrationState.GetReady or CalibrationState.Recording;
    public bool IsAnalyzing => State == CalibrationState.Analyzing;
    public bool IsResult => State == CalibrationState.Result;
    public bool IsFailed => State == CalibrationState.Failed;

    private CalibrationStep? Current => _stepIndex >= 0 && _stepIndex < Steps.Length ? Steps[_stepIndex] : null;
    public string StepHeading => Current is null ? "" : $"STEP {_stepIndex + 1} OF {Steps.Length}";
    public string StepTitle => Current?.Title ?? "";
    public string Instruction => Current?.Instruction ?? "";
    public bool CanSkip => IsCapturing && Current is { Optional: true };
    public string SkipHint => Current is { Optional: true } ? "You can skip this step." : "This step is needed.";

    public string Countdown { get => _countdown; private set => Set(ref _countdown, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public double Level { get => _level; private set => Set(ref _level, value); }
    public string ErrorText { get => _error; private set => Set(ref _error, value); }

    public bool ShowBiasChoice { get => _showBiasChoice; private set => Set(ref _showBiasChoice, value); }
    public bool BiasKeepQuiet { get => _bias == CalibrationBias.KeepQuietSpeech; set { if (value) ChooseBias(CalibrationBias.KeepQuietSpeech); } }
    public bool BiasBalanced { get => _bias == CalibrationBias.Balanced; set { if (value) ChooseBias(CalibrationBias.Balanced); } }
    public bool BiasBlockNoise { get => _bias == CalibrationBias.BlockMoreNoise; set { if (value) ChooseBias(CalibrationBias.BlockMoreNoise); } }

    // ---- flow ----

    private void Begin()
    {
        _main.SetCalibrationCapture(OnSamples, muteOutput: true);   // from here on, nothing reaches the call or stream
        _stepIndex = -1;
        foreach (var chip in Chips) { chip.IsCurrent = false; chip.IsDone = false; chip.IsSkipped = false; }
        AdvanceStep();
        _timer.Start();
    }

    private void AdvanceStep()
    {
        _stepIndex++;
        foreach (var n in new[] { nameof(StepHeading), nameof(StepTitle), nameof(Instruction), nameof(SkipHint), nameof(CanSkip) }) Raise(n);

        if (_stepIndex >= Steps.Length) { Analyze(); return; }

        for (int i = 0; i < Chips.Count; i++) Chips[i].IsCurrent = i == _stepIndex;
        Progress = 0;
        Countdown = $"Get ready… {GetReadySeconds:0}";
        _clock.Restart();
        State = CalibrationState.GetReady;
    }

    private void OnTimer()
    {
        if (State is not (CalibrationState.GetReady or CalibrationState.Recording)) return;

        if (!_main.IsRunning)
        {
            Fail("Processing stopped while calibrating. Make sure the microphone is connected, then try again.");
            return;
        }

        double elapsed = _clock.Elapsed.TotalSeconds;
        if (State == CalibrationState.GetReady)
        {
            double left = GetReadySeconds - elapsed;
            Countdown = $"Get ready… {Math.Max(1, Math.Ceiling(left)):0}";
            if (left <= 0) StartRecording();
            return;
        }

        var step = Current!;
        Progress = Math.Min(1, elapsed / step.Seconds);
        Countdown = $"Recording… {Math.Max(0, Math.Ceiling(step.Seconds - elapsed)):0} s";
        if (elapsed >= step.Seconds) FinishRecording();
    }

    private void StartRecording()
    {
        var step = Current!;
        lock (_gate)
        {
            _buffer = new float[(int)(step.Seconds * _recording.SampleRate)];
            _position = 0;
        }
        _clock.Restart();
        State = CalibrationState.Recording;
    }

    /// <summary>Runs on the audio thread for every block of raw microphone audio.</summary>
    private void OnSamples(ReadOnlySpan<float> block)
    {
        lock (_gate)
        {
            if (_buffer is null) return;
            int n = Math.Min(block.Length, _buffer.Length - _position);
            if (n <= 0) return;
            block[..n].CopyTo(_buffer.AsSpan(_position));
            _position += n;
        }
    }

    private void FinishRecording()
    {
        float[]? buffer; int position;
        lock (_gate) { buffer = _buffer; position = _position; _buffer = null; }
        if (buffer is null) return;

        if (position < buffer.Length * 0.7)
        {
            Fail("The microphone stopped delivering audio during this step. Check the connection and try again.");
            return;
        }

        _recording.Phases[Current!.Phase] = position == buffer.Length ? buffer : buffer[..position];
        Chips[_stepIndex].IsDone = true;
        AdvanceStep();
    }

    private void Skip()
    {
        if (!CanSkip) return;
        lock (_gate) _buffer = null;
        Chips[_stepIndex].IsSkipped = true;
        AdvanceStep();
    }

    private async void Analyze()
    {
        _timer.Stop();
        _main.SetCalibrationCapture(null, muteOutput: false);   // done listening: the output is live again
        foreach (var chip in Chips) chip.IsCurrent = false;
        State = CalibrationState.Analyzing;

        try
        {
            var recording = _recording;
            var profile = _baseProfile;
            _analysis = await Task.Run(() => GateCalibrator.Analyze(recording, profile));
        }
        catch (Exception ex)
        {
            Fail("Something went wrong while analysing the recordings: " + ex.Message);
            return;
        }

        PresentAnalysis(_analysis);
    }

    /// <summary>Shows the outcome of an analysis: the result page, or the failure page if it could not be used.</summary>
    public void PresentAnalysis(CalibrationAnalysis analysis)
    {
        _analysis = analysis;
        if (analysis.Error is { } error) { Fail(error); return; }

        _results.Clear();
        foreach (var bias in Enum.GetValues<CalibrationBias>()) _results[bias] = analysis.Choose(bias);

        // Only offer the choice when it makes a difference for this person's recordings.
        ShowBiasChoice = _results.Values.Select(r => (r.GateOpenDb, r.GateCloseDb, r.GateHoldMs)).Distinct().Count() > 1;
        _bias = CalibrationBias.Balanced;
        Raise(nameof(BiasKeepQuiet)); Raise(nameof(BiasBalanced)); Raise(nameof(BiasBlockNoise));
        ShowResult();
        State = CalibrationState.Result;
    }

    private void ChooseBias(CalibrationBias bias)
    {
        if (_bias == bias) return;
        _bias = bias;
        Raise(nameof(BiasKeepQuiet)); Raise(nameof(BiasBalanced)); Raise(nameof(BiasBlockNoise));
        if (_results.Count > 0) ShowResult();
    }

    private void ShowResult()
    {
        _result = _results[_bias];
        Changes.Clear();
        Changes.Add(new ChangeLine("Gate opens above", $"{_baseProfile.GateOpenDb:0} dB", $"{_result.GateOpenDb:0} dB"));
        Changes.Add(new ChangeLine("Gate closes below", $"{_baseProfile.GateCloseDb:0} dB", $"{_result.GateCloseDb:0} dB"));
        Changes.Add(new ChangeLine("Stays open for", $"{_baseProfile.GateHoldMs:0} ms", $"{_result.GateHoldMs:0} ms"));

        SummaryLines.Clear();
        foreach (var line in _result.Summary) SummaryLines.Add(line);
        AdviceLines.Clear();
        foreach (var a in _result.Advice) AdviceLines.Add(new AdviceLine(a.Severity == AdviceSeverity.Warning, a.Text));
    }

    private void Fail(string message)
    {
        _timer.Stop();
        lock (_gate) _buffer = null;
        _main.SetCalibrationCapture(null, muteOutput: false);
        ErrorText = message;
        State = CalibrationState.Failed;
    }

    private void Apply()
    {
        if (_result is null) return;
        _main.ApplyCalibration(_result);
        CloseRequested?.Invoke(true);
    }

    private void Redo()
    {
        _timer.Stop();
        _recording.Phases.Clear();
        _results.Clear();
        _analysis = null;
        _stepIndex = -1;
        foreach (var chip in Chips) { chip.IsCurrent = false; chip.IsDone = false; chip.IsSkipped = false; }
        State = CalibrationState.Intro;
        Raise(nameof(StepHeading)); Raise(nameof(StepTitle)); Raise(nameof(Instruction));
    }

    private void OnMainTick(float inDb, float outDb, bool gateOpen) => Level = inDb;

    public void Dispose()
    {
        _timer.Stop();
        _main.Tick -= OnMainTick;
        _main.SetCalibrationCapture(null, muteOutput: false);   // never leave the microphone muted behind us
    }
}
