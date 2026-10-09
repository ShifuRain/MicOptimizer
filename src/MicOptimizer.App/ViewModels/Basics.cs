using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MicOptimizer.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    private readonly Func<bool>? _canRun;

    public RelayCommand(Action run, Func<bool>? canRun = null) { _run = run; _canRun = canRun; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canRun?.Invoke() ?? true;
    public void Execute(object? parameter) => _run();
}

/// <summary>One slider: reads and writes a single profile value and tells the window when the user edits it.</summary>
public sealed class ParamVm : ObservableObject
{
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private readonly Action _edited;

    public ParamVm(string label, float min, float max, float step, string unit, string format,
                   Func<float> get, Action<float> set, Action edited)
    {
        Label = label; Min = min; Max = max; Step = step; Unit = unit; Format = format;
        _get = get; _set = set; _edited = edited;
    }

    public string Label { get; }
    public float Min { get; }
    public float Max { get; }
    public float Step { get; }
    public string Unit { get; }
    public string Format { get; }

    public double Value
    {
        get => _get();
        set
        {
            float v = Math.Clamp((float)value, Min, Max);
            if (Step > 0) v = MathF.Round(v / Step) * Step;
            if (v == _get()) return;
            _set(v);
            Raise(); Raise(nameof(Display));
            _edited();
        }
    }

    public string Display => _get().ToString(Format, CultureInfo.InvariantCulture) + (Unit.Length > 0 ? " " + Unit : "");

    public void Refresh() { Raise(nameof(Value)); Raise(nameof(Display)); }
}

/// <summary>An on/off switch inside a section (for example "Keep whispers").</summary>
public sealed class ToggleVm : ObservableObject
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly Action _edited;

    public ToggleVm(string label, Func<bool> get, Action<bool> set, Action edited)
    {
        Label = label; _get = get; _set = set; _edited = edited;
    }

    public string Label { get; }

    public bool IsOn
    {
        get => _get();
        set { if (value == _get()) return; _set(value); Raise(); _edited(); }
    }

    public void Refresh() => Raise(nameof(IsOn));
}

/// <summary>One processing stage as shown in the window: title, plain-language purpose, switches, sliders and a live readout.</summary>
public sealed class SectionVm : ObservableObject
{
    private readonly Func<bool>? _getPower;
    private readonly Action<bool>? _setPower;
    private readonly Action _edited;
    private string _live = "";

    public SectionVm(string title, string purpose, Action edited,
                     Func<bool>? getPower = null, Action<bool>? setPower = null)
    {
        Title = title; Purpose = purpose; _edited = edited; _getPower = getPower; _setPower = setPower;
    }

    public string Title { get; }
    public string Purpose { get; }
    public List<ToggleVm> Toggles { get; } = new();
    public List<ParamVm> Params { get; } = new();

    public bool HasPower => _getPower is not null;

    public bool PowerOn
    {
        get => _getPower?.Invoke() ?? true;
        set
        {
            if (_setPower is null || value == _getPower!()) return;
            _setPower(value);
            Raise(); _edited();
        }
    }

    public string Live { get => _live; set => Set(ref _live, value); }

    public void Refresh()
    {
        Raise(nameof(PowerOn));
        foreach (var t in Toggles) t.Refresh();
        foreach (var p in Params) p.Refresh();
    }
}
