using System.Windows;
using MicOptimizer.App.ViewModels;

namespace MicOptimizer.App;

public partial class CalibrationWindow : Window
{
    private readonly CalibrationViewModel _vm;

    public CalibrationWindow(ICalibrationHost main)
    {
        InitializeComponent();
        _vm = new CalibrationViewModel(main);
        _vm.CloseRequested += applied => { DialogResult = applied; };
        DataContext = _vm;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();      // also un-mutes the output if the window is closed mid-calibration
        base.OnClosed(e);
    }
}
