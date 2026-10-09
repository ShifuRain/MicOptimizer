using System.ComponentModel;
using System.Windows;
using MicOptimizer.App.ViewModels;

namespace MicOptimizer.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(Dispatcher)
        {
            AskText = (title, label, initial) => Prompt.AskText(this, title, label, initial),
            Confirm = (title, message) => Prompt.Confirm(this, title, message),
        };
        ViewModel.Tick += Trace.Push;
        DataContext = ViewModel;

        // While hidden in the tray nobody can see the meters, so the view model skips that work.
        IsVisibleChanged += (_, _) => ViewModel.UiActive = IsVisible;
        ViewModel.UiActive = false;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Set by the app so closing the window can show the "still running" hint.</summary>
    public TrayController? Tray { get; set; }

    /// <summary>True once the user chose Exit (or Windows is shutting down): the window may then really close.</summary>
    public bool IsExiting { get; set; }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ToggleFromTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowFromTray();
    }

    public void ExitApp()
    {
        IsExiting = true;
        Application.Current.Shutdown();
    }

    /// <summary>The close button hides the window; processing continues from the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
            Tray?.ShowStillRunningHint();
        }
        base.OnClosing(e);
    }
}
