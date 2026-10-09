using System.ComponentModel;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;
using MicOptimizer.App.ViewModels;

namespace MicOptimizer.App;

/// <summary>The notification-area icon and its menu: open the window, start/stop, bypass, switch profile, quit.</summary>
public sealed class TrayController : IDisposable
{
    private enum State { Stopped, Running, Bypassed }

    private readonly MainWindow _window;
    private readonly MainViewModel _vm;
    private readonly WinForms.NotifyIcon _icon;
    private readonly Dictionary<State, Drawing.Icon> _icons;
    private readonly WinForms.ToolStripMenuItem _toggleItem, _bypassItem, _startupItem;
    private readonly WinForms.ToolStripMenuItem _profilesItem;

    public TrayController(MainWindow window, MainViewModel vm)
    {
        _window = window;
        _vm = vm;

        _icons = new()
        {
            [State.Stopped] = Paint(Drawing.Color.FromArgb(0x68, 0x6B, 0x71), Drawing.Color.FromArgb(0xE6, 0xE3, 0xDB)),
            [State.Running] = Paint(Drawing.Color.FromArgb(0x1E, 0x20, 0x24), Drawing.Color.FromArgb(0xE4, 0x57, 0x2E)),
            [State.Bypassed] = Paint(Drawing.Color.FromArgb(0x1E, 0x20, 0x24), Drawing.Color.FromArgb(0x9A, 0x9D, 0xA3)),
        };

        var menu = new WinForms.ContextMenuStrip();
        var open = new WinForms.ToolStripMenuItem("Open Mic Optimizer", null, (_, _) => _window.ShowFromTray())
        {
            Font = new Drawing.Font(WinForms.SystemInformation.MenuFont, Drawing.FontStyle.Bold),
        };
        _toggleItem = new WinForms.ToolStripMenuItem("Start processing", null, (_, _) => _vm.ToggleRunCommand.Execute(null));
        _bypassItem = new WinForms.ToolStripMenuItem("Bypass (send mic unprocessed)", null, (_, _) => _vm.Bypass = !_vm.Bypass);
        _profilesItem = new WinForms.ToolStripMenuItem("Profile");
        _startupItem = new WinForms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        var exit = new WinForms.ToolStripMenuItem("Exit", null, (_, _) => _window.ExitApp());

        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            open, new WinForms.ToolStripSeparator(),
            _toggleItem, _bypassItem, _profilesItem, new WinForms.ToolStripSeparator(),
            _startupItem, new WinForms.ToolStripSeparator(), exit,
        });
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new WinForms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _window.ToggleFromTray();
        };

        _vm.PropertyChanged += OnViewModelChanged;
        RefreshIcon();
    }

    /// <summary>Shown once, the first time the window is closed to the tray, so it doesn't look like the app quit.</summary>
    public void ShowStillRunningHint()
    {
        if (_vm.Settings.TrayHintShown) return;
        _vm.Settings.TrayHintShown = true;
        _vm.SaveSettings();
        _icon.ShowBalloonTip(5000, "Mic Optimizer is still running",
            "It keeps processing your microphone from the tray. Right-click the icon to quit.", WinForms.ToolTipIcon.Info);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.Bypass)
            or nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.IsModified))
            RefreshIcon();
    }

    private void RefreshIcon()
    {
        var state = !_vm.IsRunning ? State.Stopped : _vm.Bypass ? State.Bypassed : State.Running;
        _icon.Icon = _icons[state];

        string profile = _vm.SelectedProfile?.Name ?? "no profile";
        string what = state switch { State.Stopped => "stopped", State.Bypassed => "bypass", _ => "processing" };
        string text = $"Mic Optimizer: {profile} ({what})";
        _icon.Text = text.Length <= 63 ? text : text[..63];   // NotifyIcon.Text is limited to 63 characters
    }

    private void RefreshMenu()
    {
        _toggleItem.Text = _vm.IsRunning ? "Stop processing" : "Start processing";
        _bypassItem.Checked = _vm.Bypass;
        _startupItem.Checked = StartupRegistration.IsEnabled;

        _profilesItem.DropDownItems.Clear();
        foreach (var profile in _vm.Profiles)
        {
            var item = new WinForms.ToolStripMenuItem(profile.Name) { Checked = ReferenceEquals(profile, _vm.SelectedProfile) };
            var chosen = profile;
            item.Click += (_, _) => _vm.SelectedProfile = chosen;
            _profilesItem.DropDownItems.Add(item);
        }
    }

    private void ToggleStartup()
    {
        _vm.StartWithWindows = !StartupRegistration.IsEnabled;
    }

    /// <summary>Draws the app mark (waveform bars on a rounded tile) in the colours for a given state.</summary>
    private static Drawing.Icon Paint(Drawing.Color tile, Drawing.Color bars)
    {
        const int size = 32;
        using var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var tilePath = RoundRect(0, 0, size, size, size * 0.22f);
            using var tileBrush = new Drawing.SolidBrush(tile);
            g.FillPath(tileBrush, tilePath);

            float[] heights = { 0.26f, 0.5f, 0.76f, 0.46f, 0.24f };
            float barW = size * 0.1f, gap = size * 0.055f;
            float x = (size - (heights.Length * barW + (heights.Length - 1) * gap)) / 2;
            using var barBrush = new Drawing.SolidBrush(bars);
            foreach (var h in heights)
            {
                float bh = size * h;
                using var bar = RoundRect(x, (size - bh) / 2, barW, bh, barW / 2);
                g.FillPath(barBrush, bar);
                x += barW + gap;
            }
        }

        // GetHicon creates a native handle; copy it into a managed Icon, then free the handle.
        IntPtr handle = bmp.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        var p = new Drawing2D.GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _icon.Visible = false;   // otherwise a ghost icon lingers in the tray until the mouse passes over it
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
