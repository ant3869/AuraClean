<#
.SYNOPSIS
    Window-identity logic for the published-EXE smoke test (dot-sourced by Test-PublishedExeSmoke.ps1 and Test-SmokeChecks.ps1).
    Read-only: enumerates top-level windows of one process; nothing is clicked, closed or changed.

.DESCRIPTION
    A window that merely exists and responds does not prove AuraClean started: when MainWindow initialisation fails, App.OnStartup
    shows a blocking MessageBox that is ALSO titled "AuraClean". The real application window is distinguished by its WPF window
    class (HwndWrapper[<exe>;;<guid>]), its title, visibility, state and size; any visible system dialog (class #32770, what
    MessageBox creates) belonging to the process makes the startup unacceptable.
#>

if (-not ('AuraWin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class AuraWin
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    public sealed class Info
    {
        public long Handle; public string Class; public string Title;
        public bool Visible; public bool Minimized; public int Width; public int Height;
    }

    public static List<Info> ForProcess(int pid)
    {
        var list = new List<Info>();
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != (uint)pid) return true;
            var c = new StringBuilder(256); GetClassName(h, c, c.Capacity);
            var t = new StringBuilder(512); GetWindowText(h, t, t.Capacity);
            RECT r; GetWindowRect(h, out r);
            list.Add(new Info { Handle = h.ToInt64(), Class = c.ToString(), Title = t.ToString(), Visible = IsWindowVisible(h), Minimized = IsIconic(h), Width = r.R - r.L, Height = r.B - r.T });
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
}

function Get-AppWindows([int]$ProcessId) { @([AuraWin]::ForProcess($ProcessId)) }

# Pure decision over window descriptors (objects with Class, Title, Visible, Minimized, Width, Height).
# Returns @{ Ok; Reasons } - Ok only when a genuine application window is up and no system dialog is blocking it.
function Test-AppWindow($Windows, [string]$HostName = 'AuraClean.exe', [string]$Title = 'AuraClean', [int]$MinWidth = 400, [int]$MinHeight = 300) {
    $reasons = New-Object System.Collections.Generic.List[string]
    $all = @($Windows)
    $dialogs = @($all | Where-Object { $_.Visible -and $_.Class -ceq '#32770' })
    $wpfPrefix = '^HwndWrapper\[' + [regex]::Escape($HostName) + ';'
    $main = @($all | Where-Object { $_.Visible -and -not $_.Minimized -and $_.Class -match $wpfPrefix -and $_.Title -ceq $Title -and $_.Width -ge $MinWidth -and $_.Height -ge $MinHeight })
    foreach ($d in $dialogs) { $reasons.Add("a blocking system dialog is showing (class #32770, title '$($d.Title)'): this is how a startup error appears") }
    if ($main.Count -eq 0) {
        $near = @($all | Where-Object { $_.Visible } | ForEach-Object { "[$($_.Class) '$($_.Title)' $($_.Width)x$($_.Height)]" })
        $reasons.Add("no visible application window (WPF class HwndWrapper[$HostName;...], title '$Title', at least ${MinWidth}x${MinHeight}, not minimised); visible windows: $(if ($near.Count) { $near -join ' ' } else { 'none' })")
    }
    @{ Ok = ($reasons.Count -eq 0); Reasons = @($reasons) }
}
