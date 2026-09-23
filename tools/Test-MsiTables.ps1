<#
.SYNOPSIS
  Tries to load every table in an MSI through Windows Installer and reports
  which ones it rejects. Localises "could not load table" failures to a
  specific table rather than a whole package.
#>
param([Parameter(Mandatory = $true)][string] $Path)

$full = (Resolve-Path -LiteralPath $Path).ProviderPath
$installer = New-Object -ComObject WindowsInstaller.Installer

function Call {
    param($Target, [string] $Name, [string] $Kind, $Arguments)
    $flags = if ($Kind -eq 'Method') { [System.Reflection.BindingFlags]::InvokeMethod }
             else { [System.Reflection.BindingFlags]::GetProperty }
    $Target.GetType().InvokeMember($Name, $flags, $null, $Target, $Arguments)
}

$db = Call $installer 'OpenDatabase' 'Method' @($full, 0)

# _Tables lists every table in the database.
$tables = @()
$view = Call $db 'OpenView' 'Method' @('SELECT `Name` FROM `_Tables`')
Call $view 'Execute' 'Method' $null | Out-Null
while ($true) {
    $r = Call $view 'Fetch' 'Method' $null
    if ($null -eq $r) { break }
    $tables += Call $r 'StringData' 'Get' @(1)
}

$bad = 0
foreach ($t in ($tables | Sort-Object)) {
    try {
        $v = Call $db 'OpenView' 'Method' @("SELECT * FROM ``$t``")
        Call $v 'Execute' 'Method' $null | Out-Null
        $n = 0
        while ($true) {
            $rec = Call $v 'Fetch' 'Method' $null
            if ($null -eq $rec) { break }
            $n++
        }
        '{0,-26} OK    {1,4} rows' -f $t, $n
    } catch {
        $msg = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        '{0,-26} FAIL  {1}' -f $t, ($msg -replace '\s+', ' ')
        $bad++
    }
}
Write-Output ''
Write-Output "$bad table(s) rejected out of $($tables.Count)"
exit $bad
