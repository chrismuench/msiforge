<#
.SYNOPSIS
  Builds a package, installs it, upgrades it in place, then uninstalls it.

  A structurally valid MSI can still be a broken package: the file version bug
  that deleted 100 of 136 files during an upgrade produced a database every
  reader accepted. Only msiexec finds that class of fault, so this runs the real
  lifecycle and checks what actually landed on disk.
#>
$ErrorActionPreference = 'Stop'

# RUNNER_TEMP only exists on a GitHub runner; fall back so the script can be
# run locally, which is the only way to know it works before pushing it.
$base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$work = Join-Path $base 'msimaker-ci'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$cli = Join-Path $PWD 'src\MsiMaker.App\bin\Release\net10.0\MsiMaker.exe'
if (-not (Test-Path $cli)) { throw "CLI not found at $cli" }

function Invoke-Msi {
    param([string[]] $Arguments, [string] $Label)
    $log = Join-Path $work "$Label.log"
    $p = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/l*v', $log)) -Wait -PassThru
    Write-Host "msiexec $Label -> exit $($p.ExitCode)"
    if ($p.ExitCode -ne 0) {
        Write-Host "--- errors from $log ---"
        Get-Content $log | Select-String -Pattern 'Error \d{4}|returned actual error' |
            Select-Object -First 8 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
        throw "$Label failed with exit code $($p.ExitCode)"
    }
    return $log
}

# ---------------------------------------------------------------- the payload
$payload = Join-Path $work 'payload'
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'sub') | Out-Null
Set-Content -Path (Join-Path $payload 'readme.txt') -Value 'version one' -NoNewline
Set-Content -Path (Join-Path $payload 'sub\nested.txt') -Value 'nested' -NoNewline

# A versioned PE, which is the case that exposed the File.Version bug.
Copy-Item (Join-Path $env:SystemRoot 'System32\notepad.exe') (Join-Path $payload 'tool.exe') -Force

$target = 'C:\Program Files\MsiMaker CI'
$upgradeCode = '4d9a1e77-3c52-4b18-9f06-7a2e5d3c1b40'

function Write-Project {
    param([string] $Version, [string] $Path)
    $project = [ordered]@{
        ProductName   = 'MsiMaker CI'
        Manufacturer  = 'MsiMaker'
        Version       = $Version
        UpgradeCode   = $upgradeCode
        Platform      = 'X64'
        InstallFolder = 'MsiMaker CI'
        Folders       = @(@{ SourceDir = $payload })
        Registry      = @(@{ Root = 'HKLM'; Key = 'SOFTWARE\MsiMaker\CI'; Name = 'Version'; Value = $Version })
    }
    $project | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -NoNewline
}

# ------------------------------------------------------------------ version 1
Write-Project -Version '1.0.0' -Path (Join-Path $work 'v1.json')
& $cli build (Join-Path $work 'v1.json') (Join-Path $work 'v1.msi')
if ($LASTEXITCODE -ne 0) { throw 'build of 1.0.0 failed' }

& (Join-Path $PWD 'tools\Test-MsiTables.ps1') -Path (Join-Path $work 'v1.msi')
if ($LASTEXITCODE -ne 0) { throw 'Windows Installer rejected one or more tables' }

Invoke-Msi -Arguments @('/i', (Join-Path $work 'v1.msi'), '/qn') -Label 'install' | Out-Null

$installed = @(Get-ChildItem -Recurse -File -LiteralPath $target)
Write-Host "installed $($installed.Count) file(s)"
if ($installed.Count -ne 3) { throw "expected 3 files, found $($installed.Count)" }

$registry = (Get-ItemProperty 'HKLM:\SOFTWARE\MsiMaker\CI').Version
if ($registry -ne '1.0.0') { throw "registry says '$registry', expected 1.0.0" }

# ------------------------------------------------------------------ version 2
# The payload changes, as a real new version would.
Set-Content -Path (Join-Path $payload 'readme.txt') -Value 'version two' -NoNewline
Write-Project -Version '1.1.0' -Path (Join-Path $work 'v2.json')
& $cli build (Join-Path $work 'v2.json') (Join-Path $work 'v2.msi')
if ($LASTEXITCODE -ne 0) { throw 'build of 1.1.0 failed' }

# The tool's own opinion, before Windows gets a say.
& $cli upgrade (Join-Path $work 'v1.msi') (Join-Path $work 'v2.msi')
if ($LASTEXITCODE -ne 0) { throw 'upgrade doctor did not expect an in-place upgrade' }

Invoke-Msi -Arguments @('/i', (Join-Path $work 'v2.msi'), '/qn') -Label 'upgrade' | Out-Null

$afterUpgrade = @(Get-ChildItem -Recurse -File -LiteralPath $target)
Write-Host "after upgrade: $($afterUpgrade.Count) file(s)"
if ($afterUpgrade.Count -ne 3) {
    throw "upgrade left $($afterUpgrade.Count) files, expected 3 - this is the File.Version failure mode"
}

$content = Get-Content -LiteralPath (Join-Path $target 'readme.txt') -Raw
if ($content -ne 'version two') { throw "payload was not replaced: '$content'" }

$arp = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
    ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.DisplayName -eq 'MsiMaker CI' })
if ($arp.Count -ne 1) { throw "expected exactly one entry in Programs and Features, found $($arp.Count)" }
if ($arp[0].DisplayVersion -ne '1.1.0') { throw "Programs and Features shows $($arp[0].DisplayVersion)" }
Write-Host "one entry, at $($arp[0].DisplayVersion)"

# -------------------------------------------------------------------- removal
Invoke-Msi -Arguments @('/x', (Join-Path $work 'v2.msi'), '/qn') -Label 'uninstall' | Out-Null

if (Test-Path $target) { throw "install folder survived uninstall" }
if (Test-Path 'HKLM:\SOFTWARE\MsiMaker\CI') { throw "registry key survived uninstall" }

$remaining = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
    ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.DisplayName -eq 'MsiMaker CI' })
if ($remaining.Count -ne 0) { throw "Programs and Features entry survived uninstall" }

Write-Host ''
Write-Host 'install, in-place upgrade and uninstall all verified'
