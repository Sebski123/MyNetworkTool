using System;
using System.Runtime.InteropServices;

namespace NetworkingTool.Service;

/// <summary>
/// P/Invoke wrappers. The only native call we need is broadcasting a settings-change message so
/// that already-running processes pick up the new machine-scope HTTP(S)_PROXY environment variables
/// without requiring a reboot or logoff.
/// </summary>
internal static class NativeMethods
{
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>
    /// Tells all top-level windows that the environment block changed ("Environment"), so the
    /// shell and other listeners refresh their cached environment variables.
    /// </summary>
    public static void BroadcastEnvironmentChange()
    {
        SendMessageTimeout(
            (IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 5000, out _);
    }
}
