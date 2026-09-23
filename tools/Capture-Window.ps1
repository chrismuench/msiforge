<#
.SYNOPSIS
  Launches an app, waits for its window, and saves a screenshot.
  Used to confirm the GUI actually renders rather than merely compiling.
#>
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [Parameter(Mandatory = $true)][string] $Out,
    [int] $WaitSeconds = 8
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

$p = Start-Process -FilePath $Exe -PassThru
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

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
