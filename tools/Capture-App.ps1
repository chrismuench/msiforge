<#
.SYNOPSIS
  Launches the app, optionally clicks through the wizard steps, and renders each
  page to a PNG.

  Uses PrintWindow with PW_RENDERFULLCONTENT rather than CopyFromScreen, because
  the latter needs an interactive desktop and fails with "the handle is invalid"
  from a service or detached session. PrintWindow asks the window to draw itself,
  which works regardless - and captures the window alone, without whatever else
  happens to be on the desktop.
#>
param(
    [string] $Exe = ".\dist\win-x64\MsiMaker.exe",
    [string] $OutDir = "$env:TEMP\msimaker-ui",
    [string[]] $Arguments = @(),
    [int[]] $StepX = @(),
    [int] $StepY = 104,
    [int] $WaitSeconds = 30
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class AppShot {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public const uint RenderFullContent = 0x00000002;

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(140);
        mouse_event(0x02, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
    }
}
'@

# The bitmap work stays in PowerShell: referencing System.Drawing.Common from
# Add-Type is awkward on PowerShell 7, and the P/Invokes above are all the
# native surface actually needed.
function Save-Window {
    param([IntPtr] $Handle, [string] $Path)
    $r = New-Object AppShot+RECT
    if (-not [AppShot]::GetWindowRect($Handle, [ref]$r)) { return $false }
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { return $false }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $dc = $g.GetHdc()
        try { $ok = [AppShot]::PrintWindow($Handle, $dc, [AppShot]::RenderFullContent) }
        finally { $g.ReleaseHdc($dc) }
        if (-not $ok) { return $false }
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        return $true
    } finally {
        $g.Dispose(); $bmp.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue |
    Stop-Process -Force
Start-Sleep -Milliseconds 900

$start = @{ FilePath = $Exe; PassThru = $true }
if ($Arguments.Count -gt 0) { $start['ArgumentList'] = $Arguments }
$p = Start-Process @start

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) {
    Start-Sleep -Milliseconds 400
    $p.Refresh()
}
if ($p.MainWindowHandle -eq 0) { Write-Output "window never opened"; exit 1 }

[AppShot]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 2500

$r = New-Object AppShot+RECT
[AppShot]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
Write-Output "window $($r.R - $r.L) x $($r.B - $r.T)"

$index = 0
$path = Join-Path $OutDir ("step-{0}.png" -f $index)
Write-Output $(if (Save-Window -Handle $p.MainWindowHandle -Path $path) { "saved $path" } else { "render failed for $path" })

foreach ($x in $StepX) {
    $index++
    [AppShot]::Click($r.L + $x, $r.T + $StepY)
    Start-Sleep -Milliseconds 900
    $path = Join-Path $OutDir ("step-{0}.png" -f $index)
    Write-Output $(if (Save-Window -Handle $p.MainWindowHandle -Path $path) { "saved $path" } else { "render failed for $path" })
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
