using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using MicOptimizer.Audio;
using MicOptimizer.Core.Calibration;
using MicOptimizer.Core.Dsp;
using MicOptimizer.Core.Profiles;

namespace MicOptimizer.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable, ICalibrationHost
{
    private const float MeterFloorDb = -70f;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _reconnect;
    private bool _wantRunning, _cableMissing;
    private int _summaryTicks;
    private readonly ProfileStore _store = new();
    private readonly AudioEngine _engine = new();
    private readonly AppSettings _settings;

    private Profile _working = new();
    private Profile? _selectedProfile;
    private bool _loading;
    private bool _isModified, _isRunning, _bypass, _gateOpen;
    private float _inDb = MeterFloorDb, _outDb = MeterFloorDb;
    private int _silentTicks;
    private string _status = "";
    private AudioDevice? _selectedInput, _selectedOutput;
    private int _inputChannel;

    /// <summary>Set by the window: asks for a line of text, returns null if cancelled.</summary>
    public Func<string, string, string, string?>? AskText { get; set; }
    /// <summary>Set by the window: asks a yes/no question.</summary>
    public Func<string, string, bool>? Confirm { get; set; }
    /// <summary>Raised ~30 times a second with (input dB, output dB, gate open) for the trace.</summary>
    public event Action<float, float, bool>? Tick;

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        _settings = AppSettings.Load();
        _inputChannel = Math.Clamp(_settings.InputChannel, 0, 1);

        ToggleRunCommand = new RelayCommand(ToggleRun);
        SaveCommand = new RelayCommand(Save, () => IsModified && !IsBuiltIn);
        SaveAsCommand = new RelayCommand(SaveAs);
        RevertCommand = new RelayCommand(() => { if (SelectedProfile is { } p) LoadProfile(p); }, () => IsModified);
        DeleteCommand = new RelayCommand(Delete, () => !IsBuiltIn);
        FindMicCommand = new RelayCommand(async () => await FindMic(), () => SelectedInput is not null);
        RefreshDevicesCommand = new RelayCommand(() => RefreshDevices(keepSelection: true));
        CalibrateCommand = new RelayCommand(RequestCalibration);

        BuildSections();
        LoadProfiles(_settings.LastProfile);
        RefreshDevices(keepSelection: false);

        _engine.Faulted += message => _ui.BeginInvoke(() => OnFaulted(message));
        _timer = new DispatcherTimer(DispatcherPriority.Render, _ui) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        // At login the audio interface can still be starting up, and it can be unplugged later. While the user wants
        // processing on, keep trying to (re)connect instead of giving up.
        _reconnect = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(4) };
        _reconnect.Tick += (_, _) => TryReconnect();
        _reconnect.Start();

        _wantRunning = _settings.AutoStart;
        if (!_wantRunning) Status = "Press Start to begin processing.";
        else if (SelectedInput is not null && SelectedOutput is not null) Start();
        else Status = WaitingMessage;
    }

    private const string WaitingMessage = "Waiting for the audio interface and the virtual cable to appear…";

    /// <summary>The settings file, shared with the tray so only one copy is ever written.</summary>
    public AppSettings Settings => _settings;
    public void SaveSettings() => _settings.Save();

    /// <summary>False while the window is hidden in the tray: meters and the trace aren't needed then.</summary>
    public bool UiActive { get; set; } = true;

    public bool CableMissing { get => _cableMissing; private set => Set(ref _cableMissing, value); }

    // ---- calibration ----

    /// <summary>Raised when the calibration wizard should open; the window shows it.</summary>
    public event Action? CalibrationRequested;
    public ICommand CalibrateCommand { get; }

    /// <summary>Opens the wizard. It needs live audio, so processing is started first if it isn't running.</summary>
    public void RequestCalibration()
    {
        if (!IsRunning) { _wantRunning = true; Start(); }
        if (!IsRunning)
        {
            Status = "Calibration needs the microphone running. Choose a microphone and an output, then try again.";
            return;
        }
        CalibrationRequested?.Invoke();
    }

    public int EngineSampleRate => _engine.SampleRate;

    /// <summary>A copy of the settings being edited right now (the calibration measures through these).</summary>
    public Profile WorkingProfileSnapshot() => _working.Clone();

    /// <summary>Listens to the raw microphone, optionally silencing the output so a call doesn't hear the test sounds.</summary>
    public void SetCalibrationCapture(AudioEngine.InputTapHandler? tap, bool muteOutput)
    {
        _engine.InputTap = tap;
        _engine.OutputMuted = muteOutput;
    }

    /// <summary>Puts the calibrated gate settings into the profile being edited, which then shows as Edited.</summary>
    public void ApplyCalibration(CalibrationResult result)
    {
        result.ApplyTo(_working);
        IsModified = true;
        foreach (var s in Sections) s.Refresh();
        ApplyLive();
        Status = "Calibration applied to the current profile. Press Save to keep it, or Revert to undo.";
    }

    // ---- latency ----

    public string[] LatencyChoices { get; } =
    {
        "Lowest (ASIO input, switches to Standard if unavailable)",
        "Standard (Windows audio)",
    };

    public int LatencyModeIndex
    {
        get => string.Equals(_settings.LatencyMode, "Standard", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        set
        {
            _settings.LatencyMode = value == 1 ? "Standard" : "Lowest";
            _settings.Save();
            Raise();
            if (IsRunning) Start();
        }
    }

    private LatencyMode RequestedMode => LatencyModeIndex == 0 ? LatencyMode.Lowest : LatencyMode.Standard;

    private string _latencySummary = "";
    /// <summary>One line saying where the delay in this app comes from, refreshed while the window is open.</summary>
    public string LatencySummary { get => _latencySummary; private set => Set(ref _latencySummary, value); }

    private const string CablePanelPath = @"C:\Program Files\VB\CABLE\VBCABLE_ControlPanel.exe";
    public bool CablePanelAvailable => File.Exists(CablePanelPath);

    /// <summary>VB-Cable buffers audio itself (about 60 ms at its default setting); its control panel can lower that.</summary>
    public ICommand OpenCablePanelCommand { get; } = new RelayCommand(() =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CablePanelPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { /* the user cancelled the elevation prompt or it was removed */ }
    });

    private void UpdateLatencySummary()
    {
        var l = _engine.Latency;
        if (l is null) { LatencySummary = ""; return; }
        var text = $"Delay in this app: {l.TotalMs:0} ms  =  input {l.InputMs:0.#} ({l.InputPath}) + processing {l.ProcessingMs:0.#} + buffer {l.BufferMs:0.#} + output {l.OutputMs:0.#}";
        if (_engine.ModeNote is { Length: > 0 } note) text += $".  ASIO not used: {note}";
        LatencySummary = text;
    }

    public ICommand OpenCableDownloadCommand { get; } = new RelayCommand(() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }));

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            if (!StartupRegistration.Set(value))
                Status = "Windows did not allow changing the startup setting for this account.";
            Raise();
        }
    }

    private void TryReconnect()
    {
        if (!_wantRunning || IsRunning) return;
        RefreshDevices(keepSelection: false);
        if (SelectedInput is not null && SelectedOutput is not null) Start();
        else Status = WaitingMessage;
    }

    // ---- state shown in the window ----

    public ObservableCollection<Profile> Profiles { get; } = new();
    public ObservableCollection<AudioDevice> Inputs { get; } = new();
    public ObservableCollection<AudioDevice> Outputs { get; } = new();
    public ObservableCollection<SectionVm> Sections { get; } = new();
    public string[] ChannelChoices { get; } = { "Input 1 (left)", "Input 2 (right)" };

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value) && !_loading && value is not null) LoadProfile(value); }
    }

    public bool IsModified { get => _isModified; private set { if (Set(ref _isModified, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsBuiltIn => SelectedProfile is not null && ProfileStore.IsBuiltIn(SelectedProfile.Name);
    public string ProfileDescription => SelectedProfile?.Description ?? "";

    public AudioDevice? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (!Set(ref _selectedInput, value)) return;
            _settings.InputDeviceId = value?.Id; _settings.Save();
            CommandManager.InvalidateRequerySuggested();
            if (IsRunning) Start();
        }
    }

    public AudioDevice? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!Set(ref _selectedOutput, value)) return;
            _settings.OutputDeviceId = value?.Id; _settings.Save();
            if (IsRunning) Start();
        }
    }

    public int InputChannel
    {
        get => _inputChannel;
        set
        {
            if (!Set(ref _inputChannel, value)) return;
            _settings.InputChannel = value; _settings.Save();
            if (IsRunning) Start();
        }
    }

    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }

    public bool Bypass
    {
        get => _bypass;
        set
        {
            if (!Set(ref _bypass, value)) return;
            if (_engine.Chain is { } c) c.Bypass = value;
            if (IsRunning) Status = value ? "Bypass is on: your microphone is sent unprocessed." : "Processing is on.";
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public float InputDb { get => _inDb; private set => Set(ref _inDb, value); }
    public float OutputDb { get => _outDb; private set => Set(ref _outDb, value); }
    public bool GateOpen { get => _gateOpen; private set => Set(ref _gateOpen, value); }

    public ICommand ToggleRunCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand FindMicCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    // ---- sections (one per processing stage) ----

    private void BuildSections()
    {
        void Edited() { IsModified = true; ApplyLive(); }

        ParamVm P(string label, float min, float max, float step, string unit, string fmt, Func<float> get, Action<float> set) =>
            new(label, min, max, step, unit, fmt, get, set, Edited);

        var input = new SectionVm("INPUT", "Extra gain after the audio interface, and a cut for desk rumble.", Edited);
        input.Params.Add(P("Mic gain", -12, 18, 0.5f, "dB", "+0.0;-0.0;0.0", () => _working.InputGainDb, v => _working.InputGainDb = v));
        input.Params.Add(P("Rumble cut", 40, 200, 5, "Hz", "0", () => _working.HighPassHz, v => _working.HighPassHz = v));

        var noise = new SectionVm("NOISE REMOVAL", "AI cleanup of fans, hiss and keyboard under your voice. Adds 10 ms of delay.", Edited,
            () => _working.NoiseSuppression, v => _working.NoiseSuppression = v);
        noise.Toggles.Add(new ToggleVm("Keep whispers", () => _working.NoiseAdaptive, v => _working.NoiseAdaptive = v, Edited));
        noise.Params.Add(P("Strength when whispers are not kept", 0, 1, 0.05f, "", "0.00", () => _working.NoiseStrength, v => _working.NoiseStrength = v));

        var gate = new SectionVm("GATE", "Turns the mic down between words so typing and clicks fall away.", Edited,
            () => _working.GateEnabled, v => _working.GateEnabled = v);
        gate.Params.Add(P("Opens above", -70, -20, 1, "dB", "0", () => _working.GateOpenDb, v => _working.GateOpenDb = v));
        gate.Params.Add(P("Closes below", -75, -25, 1, "dB", "0", () => _working.GateCloseDb, v => _working.GateCloseDb = v));
        gate.Params.Add(P("Stays open for", 0, 400, 10, "ms", "0", () => _working.GateHoldMs, v => _working.GateHoldMs = v));
        gate.Params.Add(P("Turns down by", -60, -6, 1, "dB", "0", () => _working.GateReductionDb, v => _working.GateReductionDb = v));

        var eq = new SectionVm("TONE", "Gentle shaping for a clearer, less boxy voice.", Edited,
            () => _working.EqEnabled, v => _working.EqEnabled = v);
        eq.Params.Add(P("Warmth cut (250 Hz)", -8, 4, 0.5f, "dB", "+0.0;-0.0;0.0", () => _working.EqMudDb, v => _working.EqMudDb = v));
        eq.Params.Add(P("Clarity (3.5 kHz)", -4, 8, 0.5f, "dB", "+0.0;-0.0;0.0", () => _working.EqPresenceDb, v => _working.EqPresenceDb = v));
        eq.Params.Add(P("Air (above 10 kHz)", -4, 8, 0.5f, "dB", "+0.0;-0.0;0.0", () => _working.EqAirDb, v => _working.EqAirDb = v));

        var leveler = new SectionVm("AUTO LEVEL", "Slowly evens out soft and loud passages. A small range keeps whispers soft.", Edited,
            () => _working.LevelerEnabled, v => _working.LevelerEnabled = v);
        leveler.Params.Add(P("Target level", -40, -10, 1, "dB", "0", () => _working.LevelerTargetDb, v => _working.LevelerTargetDb = v));
        leveler.Params.Add(P("Lifts quiet speech by up to", 0, 20, 0.5f, "dB", "0.0", () => _working.LevelerMaxBoostDb, v => _working.LevelerMaxBoostDb = v));
        leveler.Params.Add(P("Pulls loud speech down by up to", -20, 0, 0.5f, "dB", "0.0", () => _working.LevelerMaxCutDb, v => _working.LevelerMaxCutDb = v));

        var comp = new SectionVm("COMPRESSOR", "Catches fast loud moments so the level stays steady.", Edited,
            () => _working.CompressorEnabled, v => _working.CompressorEnabled = v);
        comp.Params.Add(P("Starts at", -40, 0, 1, "dB", "0", () => _working.CompressorThresholdDb, v => _working.CompressorThresholdDb = v));
        comp.Params.Add(P("Squeeze", 1, 10, 0.1f, ": 1", "0.0", () => _working.CompressorRatio, v => _working.CompressorRatio = v));
        comp.Params.Add(P("Make-up gain", -6, 12, 0.5f, "dB", "+0.0;-0.0;0.0", () => _working.CompressorMakeupDb, v => _working.CompressorMakeupDb = v));

        var deEss = new SectionVm("DE-ESSER", "Softens harsh \"s\" sounds. Only acts while you speak normally, never on whispers.", Edited,
            () => _working.DeEsserEnabled, v => _working.DeEsserEnabled = v);
        deEss.Params.Add(P("Reacts at", -20, 0, 1, "dB", "0", () => _working.DeEsserThresholdDb, v => _working.DeEsserThresholdDb = v));
        deEss.Params.Add(P("Softens by up to", 0, 12, 0.5f, "dB", "0.0", () => _working.DeEsserMaxReductionDb, v => _working.DeEsserMaxReductionDb = v));

        var limiter = new SectionVm("LIMITER", "A hard ceiling so shouting never clips.", Edited,
            () => _working.LimiterEnabled, v => _working.LimiterEnabled = v);
        limiter.Params.Add(P("Ceiling", -12, 0, 0.5f, "dB", "0.0", () => _working.LimiterCeilingDb, v => _working.LimiterCeilingDb = v));

        foreach (var s in new[] { input, noise, gate, eq, leveler, comp, deEss, limiter }) Sections.Add(s);
    }

    private void UpdateLiveReadouts()
    {
        var c = _engine.Chain;
        foreach (var s in Sections)
        {
            s.Live = c is null ? "" : s.Title switch
            {
                "NOISE REMOVAL" => c.Rnnoise is { Enabled: true } r
                    ? (r.Blend > 0.5f ? "Hearing speech: cleaning" : "No speech: passing through")
                    : "Off",
                "GATE" => c.Gate.Enabled ? (c.Gate.IsOpen ? "Open" : "Closed") : "Off",
                "AUTO LEVEL" => c.Leveler.Enabled ? $"Gain {c.Leveler.CurrentGainDb:+0.0;-0.0;0.0} dB" : "Off",
                "COMPRESSOR" => c.Compressor.Enabled ? $"Reducing {-c.Compressor.GainReductionDb:0.0} dB" : "Off",
                "DE-ESSER" => c.DeEsser.Enabled ? $"Reducing {c.DeEsser.ReductionDb:0.0} dB" : "Off",
                _ => "",
            };
        }
    }

    // ---- profiles ----

    private void LoadProfiles(string? select)
    {
        _loading = true;
        Profiles.Clear();
        foreach (var p in _store.LoadAll()) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == select) ?? Profiles[0];
        _loading = false;
        LoadProfile(SelectedProfile!);
    }

    private void LoadProfile(Profile profile)
    {
        _working = profile.Clone();
        IsModified = false;
        foreach (var s in Sections) s.Refresh();
        Raise(nameof(IsBuiltIn)); Raise(nameof(ProfileDescription));
        ApplyLive();
        _settings.LastProfile = profile.Name; _settings.Save();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyLive()
    {
        if (_engine.Chain is { } c)
        {
            _working.ApplyTo(c);
            c.Bypass = Bypass;
        }
    }

    private void Save()
    {
        _store.Save(_working);
        LoadProfiles(_working.Name);
        Status = $"Saved \"{_working.Name}\".";
    }

    private void SaveAs()
    {
        var suggestion = IsBuiltIn ? "My " + _working.Name : _working.Name;
        var name = AskText?.Invoke("Save profile", "Name for this profile", suggestion)?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (ProfileStore.IsBuiltIn(name))
        {
            Status = $"\"{name}\" is a built-in profile. Pick a different name.";
            return;
        }
        if (Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) &&
            Confirm?.Invoke("Replace profile", $"A profile called \"{name}\" already exists. Replace it?") != true)
            return;

        _working.Name = name;
        _store.Save(_working);
        LoadProfiles(name);
        Status = $"Saved \"{name}\".";
    }

    private void Delete()
    {
        if (SelectedProfile is not { } p || ProfileStore.IsBuiltIn(p.Name)) return;
        if (Confirm?.Invoke("Delete profile", $"Delete \"{p.Name}\"? This cannot be undone.") != true) return;
        _store.Delete(p.Name);
        LoadProfiles(BuiltInProfiles.Podcast);
        Status = $"Deleted \"{p.Name}\".";
    }

    // ---- devices and engine ----

    private void RefreshDevices(bool keepSelection)
    {
        string? inId = keepSelection ? SelectedInput?.Id : _settings.InputDeviceId;
        string? outId = keepSelection ? SelectedOutput?.Id : _settings.OutputDeviceId;

        Inputs.Clear(); Outputs.Clear();
        foreach (var d in AudioEngine.ListInputs()) Inputs.Add(d);
        foreach (var d in AudioEngine.ListOutputs()) Outputs.Add(d);

        // Remembered device first; otherwise a sensible guess (an audio interface in, the virtual cable out).
        var input = Inputs.FirstOrDefault(d => d.Id == inId)
                    ?? Inputs.FirstOrDefault(d => d.Name.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
        var output = Outputs.FirstOrDefault(d => d.Id == outId)
                     ?? Outputs.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));

        CableMissing = !Outputs.Any(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));

        // Remember what we settled on, so the next launch picks the same devices.
        if (input is not null && _settings.InputDeviceId != input.Id) { _settings.InputDeviceId = input.Id; _settings.Save(); }
        if (output is not null && _settings.OutputDeviceId != output.Id) { _settings.OutputDeviceId = output.Id; _settings.Save(); }

        var wasRunning = IsRunning;
        _selectedInput = input; _selectedOutput = output;     // set fields: no restart while refreshing
        Raise(nameof(SelectedInput)); Raise(nameof(SelectedOutput));
        CommandManager.InvalidateRequerySuggested();
        if (wasRunning && (input is null || output is null)) Stop("A selected device is no longer available.");
    }

    private void ToggleRun()
    {
        if (IsRunning) { _wantRunning = false; Stop("Stopped."); }
        else { _wantRunning = true; Start(); }
    }

    private void Start()
    {
        if (SelectedInput is null || SelectedOutput is null)
        {
            Status = "Choose a microphone and an output first.";
            return;
        }
        try
        {
            _engine.Start(SelectedInput.Id, SelectedOutput.Id, InputChannel, RequestedMode, c =>
            {
                _working.ApplyTo(c);
                c.Bypass = Bypass;
            });
            IsRunning = true;
            _silentTicks = 0;
            Status = $"Processing {SelectedInput.Name} → {SelectedOutput.Name}.";
            UpdateLatencySummary();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = "Could not start: " + ex.Message + " Close other apps that hold the device exclusively, then try again.";
        }
    }

    private void Stop(string message)
    {
        _engine.Stop();
        IsRunning = false;
        Status = message;
    }

    private void OnFaulted(string message)
    {
        if (!IsRunning) return;
        Stop($"Stopped: {message}. Will reconnect automatically when the interface is back.");
    }

    private async Task FindMic()
    {
        if (SelectedInput is null) return;
        Status = "Listening for 3 seconds. Speak into the microphone now.";
        try
        {
            var peaks = await AudioEngine.MeasureChannelsAsync(SelectedInput.Id, 3000);
            int best = 0;
            for (int i = 1; i < peaks.Length; i++) if (peaks[i] > peaks[best]) best = i;
            if (Db.ToDb(peaks[best]) < -60f)
            {
                Status = "Heard nothing on either input. Check the 48V button, the cable and the gain knob on the interface.";
                return;
            }
            if (best >= ChannelChoices.Length) best = 0;
            InputChannel = best;
            Raise(nameof(InputChannel));
            Status = $"Found your microphone on input {best + 1}.";
        }
        catch (Exception ex)
        {
            Status = "Could not listen to the input: " + ex.Message;
        }
    }

    // ---- live meters ----

    private static float ToMeterDb(float linear) => Math.Max(MeterFloorDb, Db.ToDb(linear));

    private void OnTick()
    {
        if (!UiActive) return;
        if (!IsRunning)
        {
            InputDb = MeterFloorDb; OutputDb = MeterFloorDb; GateOpen = false;
            foreach (var s in Sections) s.Live = "";
            return;
        }

        var (inPeak, outPeak) = _engine.TakePeaks();
        float inDb = ToMeterDb(inPeak), outDb = ToMeterDb(outPeak);

        // Instant attack, steady fall (about 40 dB per second), like a hardware meter.
        InputDb = Math.Max(inDb, InputDb - 1.3f);
        OutputDb = Math.Max(outDb, OutputDb - 1.3f);
        GateOpen = _engine.Chain?.Gate.IsOpen ?? false;

        UpdateLiveReadouts();
        if (++_summaryTicks % 15 == 0) UpdateLatencySummary();   // twice a second is plenty
        Tick?.Invoke(inDb, outDb, GateOpen);

        // A mic that is never louder than the noise floor usually means the wrong input channel is selected.
        _silentTicks = inDb < -66f ? _silentTicks + 1 : 0;
        if (_silentTicks == 30 * 6)
            Status = "Nothing is coming in on this input. Press \"Find my mic\", or check the 48V button and gain knob.";
    }

    public void Dispose()
    {
        _timer.Stop();
        _reconnect.Stop();
        _engine.Dispose();
        _settings.Save();
    }
}
