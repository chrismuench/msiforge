<#
.SYNOPSIS
  Launches the GUI, clicks each tab in turn and saves a screenshot of each.

  Avalonia draws its own tab strip, so there is no window handle per tab to
  address; clicking at the strip's coordinates is the reliable way to drive it
  from outside the process.
#>
param(
    [string] $Exe = ".\dist\win-x64\MsiForge.exe",
    [string] $OutDir = "$env:TEMP\msiforge-ui",
    [int] $WaitSeconds = 25
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Ui {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04;
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
}
'@

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-Process -Name 'MsiForge' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

$p = Start-Process -FilePath $Exe -PassThru
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) {
    Start-Sleep -Milliseconds 400
    $p.Refresh()
}
if ($p.MainWindowHandle -eq 0) { Write-Output "window never opened"; exit 1 }

[Ui]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 1500

$r = New-Object Ui+RECT
[Ui]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L
$h = $r.B - $r.T
Write-Output "window $w x $h at $($r.L),$($r.T)"

# Tab-strip coordinates, measured from a capture of the window.
$tabY = 104
$tabs = @(
    @{ Name = '1-payload';  X = 61  },
    @{ Name = '2-identity'; X = 137 },
    @{ Name = '3-run';      X = 200 },
    @{ Name = '4-upgrade';  X = 288 },
    @{ Name = '5-build';    X = 378 }
)

foreach ($t in $tabs) {
    [Ui]::Click($r.L + $t.X, $r.T + $tabY)
    Start-Sleep -Milliseconds 700
    [Ui]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
        $path = Join-Path $OutDir ("{0}.png" -f $t.Name)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "saved $path"
    } catch {
        Write-Output "capture failed for $($t.Name): $($_.Exception.Message)"
    } finally {
        $g.Dispose(); $bmp.Dispose()
    }
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
