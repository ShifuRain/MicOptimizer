using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MicOptimizer.Core.PathSetup;

/// <summary>
/// The current user's PATH (HKCU\Environment). Only the user's own PATH is edited, never the machine-wide one, so no
/// administrator rights are needed. New terminals pick the change up; already-open ones do not.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserPath
{
    private readonly string _subKey;

    /// <param name="subKey">Defaults to the real user environment; tests pass a throwaway key instead.</param>
    public UserPath(string subKey = "Environment") => _subKey = subKey;

    public bool Contains(string folder) => PathEditor.Contains(Read().value, folder);

    /// <summary>Returns true if the PATH was changed (false when the folder was already listed).</summary>
    public bool Add(string folder)
    {
        var (value, kind) = Read();
        var updated = PathEditor.Add(value, folder);
        if (updated == (value ?? "")) return false;
        Write(updated, kind);
        return true;
    }

    /// <summary>Returns true if the PATH was changed (false when the folder was not listed).</summary>
    public bool Remove(string folder)
    {
        var (value, kind) = Read();
        if (!PathEditor.Contains(value, folder)) return false;
        Write(PathEditor.Remove(value, folder), kind);
        return true;
    }

    /// <summary>The raw value, with %VARIABLES% left unexpanded so they are written back untouched.</summary>
    private (string? value, RegistryValueKind kind) Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        if (key is null || key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
            return (null, RegistryValueKind.ExpandString);
        return (value, key.GetValueKind("Path"));
    }

    private void Write(string value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue("Path", value, kind);
        if (_subKey == "Environment") BroadcastChange();
    }

    /// <summary>Tells Explorer and other programs the environment changed, so terminals opened from now on see it.</summary>
    private static void BroadcastChange()
    {
        const int HwndBroadcast = 0xFFFF, WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002;
        SendMessageTimeout((IntPtr)HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 1000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
