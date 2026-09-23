<#
.SYNOPSIS
  Launches an app, waits for its window, reports whether that window actually
  fits the screen, and saves a screenshot.

  Reporting the window rectangle alone is not enough: a window taller than the
  display still reports a perfectly reasonable size, and a screenshot taken by
  that rectangle looks complete while the real window has its lower half - the
  primary action and the status bar - off the bottom of the screen. The fit
  check below is the part that catches that.
#>
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string] $Out,
    [int] $WaitSeconds = 20,
    [string[]] $Arguments = @()
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

$start = @{ FilePath = $Exe; PassThru = $true }
if ($Arguments.Count -gt 0) { $start['ArgumentList'] = $Arguments }
$p = Start-Process @start

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) {
    Start-Sleep -Milliseconds 400
    $p.Refresh()
}

if ($p.MainWindowHandle -eq 0) {
    Write-Output "no window appeared within $WaitSeconds s (exited: $($p.HasExited))"
    if ($p.HasExited) { Write-Output "exit code: $($p.ExitCode)" }
    exit 1
}

[Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 1200

$r = New-Object Win+RECT
[Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L
$h = $r.B - $r.T
Write-Output "window $w x $h at $($r.L),$($r.T)"

# The working area excludes the taskbar, which is the space a window may use.
$screen = [System.Windows.Forms.Screen]::FromHandle($p.MainWindowHandle)
$wa = $screen.WorkingArea
Write-Output "working area $($wa.Width) x $($wa.Height) at $($wa.X),$($wa.Y)"

$problems = @()
if ($w -gt $wa.Width)  { $problems += "wider than the screen by $($w - $wa.Width)px" }
if ($h -gt $wa.Height) { $problems += "taller than the screen by $($h - $wa.Height)px" }
if ($r.L -lt $wa.X)    { $problems += "left edge off-screen by $($wa.X - $r.L)px" }
if ($r.T -lt $wa.Y)    { $problems += "top edge off-screen by $($wa.Y - $r.T)px" }
if (($r.R) -gt ($wa.X + $wa.Width))  { $problems += "right edge off-screen by $(($r.R) - ($wa.X + $wa.Width))px" }
if (($r.B) -gt ($wa.Y + $wa.Height)) { $problems += "bottom edge off-screen by $(($r.B) - ($wa.Y + $wa.Height))px" }

if ($problems.Count -gt 0) {
    Write-Output "DOES NOT FIT: $($problems -join '; ')"
} else {
    Write-Output "fits the screen"
}

if ($Out) {
    # Capture only the part that is actually on screen, so the image shows what
    # the user can really see rather than the window's notional rectangle.
    $x = [Math]::Max($r.L, $wa.X)
    $y = [Math]::Max($r.T, $wa.Y)
    $cw = [Math]::Min($r.R, $wa.X + $wa.Width) - $x
    $ch = [Math]::Min($r.B, $wa.Y + $wa.Height) - $y

    if ($cw -gt 0 -and $ch -gt 0) {
        $bmp = New-Object System.Drawing.Bitmap $cw, $ch
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
            $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output "saved $Out (visible region $cw x $ch)"
        } catch {
            Write-Output "capture failed: $($_.Exception.Message)"
        } finally {
            $g.Dispose(); $bmp.Dispose()
        }
    }
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
if ($problems.Count -gt 0) { exit 2 }
