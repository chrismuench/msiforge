<#
.SYNOPSIS
  Opens an MSI through Windows Installer itself (msi.dll) and dumps key tables.

  MsiMaker writes MSIs with its own managed engine, so the authority on whether
  the output is well formed is Windows Installer, not our own reader. This is the
  acceptance check for anything the engine emits.
#>
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [string] $Query = 'SELECT `Property`, `Value` FROM `Property`'
)

$full = (Resolve-Path -LiteralPath $Path).ProviderPath
$installer = New-Object -ComObject WindowsInstaller.Installer

function Invoke-Member {
    param($Target, [string] $Name, [string] $Type, $Arguments)
    $flags = switch ($Type) {
        'Method'   { [System.Reflection.BindingFlags]::InvokeMethod }
        'Get'      { [System.Reflection.BindingFlags]::GetProperty }
    }
    $Target.GetType().InvokeMember($Name, $flags, $null, $Target, $Arguments)
}

try {
    # 0 = msiOpenDatabaseModeReadOnly
    $db = Invoke-Member $installer 'OpenDatabase' 'Method' @($full, 0)
} catch {
    Write-Output "REJECTED by Windows Installer: $($_.Exception.InnerException.Message ?? $_.Exception.Message)"
    exit 1
}

Write-Output "ACCEPTED by Windows Installer (msi.dll opened the database)"

try {
    $view = Invoke-Member $db 'OpenView' 'Method' @($Query)
    Invoke-Member $view 'Execute' 'Method' $null | Out-Null
    while ($true) {
        $record = Invoke-Member $view 'Fetch' 'Method' $null
        if ($null -eq $record) { break }
        $count = Invoke-Member $record 'FieldCount' 'Get' $null
        $cells = for ($i = 1; $i -le $count; $i++) { Invoke-Member $record 'StringData' 'Get' @($i) }
        Write-Output ('   ' + ($cells -join '  |  '))
    }
} catch {
    Write-Output "  query failed: $($_.Exception.InnerException.Message ?? $_.Exception.Message)"
    exit 1
}

# Summary information is a separate COM surface and a common source of corruption.
try {
    $si = Invoke-Member $installer 'SummaryInformation' 'Get' @($full, 0)
    $template = Invoke-Member $si 'Property' 'Get' @(7)
    $revision = Invoke-Member $si 'Property' 'Get' @(9)
    $pageCount = Invoke-Member $si 'Property' 'Get' @(14)
    $wordCount = Invoke-Member $si 'Property' 'Get' @(15)
    Write-Output "   --"
    Write-Output "   Template(7)=$template  PackageCode(9)=$revision  Schema(14)=$pageCount  SourceFlags(15)=$wordCount"
} catch {
    Write-Output "  summary information unreadable: $($_.Exception.InnerException.Message ?? $_.Exception.Message)"
    exit 1
}
exit 0
