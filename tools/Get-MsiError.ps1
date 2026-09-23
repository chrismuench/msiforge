<#
.SYNOPSIS
  Runs a query against an MSI and prints Windows Installer's own error record,
  which names the failing table/column instead of a generic COM message.
#>
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [Parameter(Mandatory = $true)][string[]] $Queries
)

$full = (Resolve-Path -LiteralPath $Path).ProviderPath
$installer = New-Object -ComObject WindowsInstaller.Installer

function Call {
    param($Target, [string] $Name, [string] $Kind, $Arguments)
    $flags = if ($Kind -eq 'Method') { [System.Reflection.BindingFlags]::InvokeMethod }
             else { [System.Reflection.BindingFlags]::GetProperty }
    $Target.GetType().InvokeMember($Name, $flags, $null, $Target, $Arguments)
}

$db = Call $installer 'OpenDatabase' 'Method' @($full, 0)

foreach ($q in $Queries) {
    try {
        $v = Call $db 'OpenView' 'Method' @($q)
        Call $v 'Execute' 'Method' $null | Out-Null
        $n = 0
        while ($true) { if ($null -eq (Call $v 'Fetch' 'Method' $null)) { break }; $n++ }
        '{0,-58} OK ({1} rows)' -f $q, $n
    } catch {
        $detail = ''
        try {
            $rec = Call $installer 'LastErrorRecord' 'Get' $null
            if ($rec) {
                $count = Call $rec 'FieldCount' 'Get' $null
                $fields = for ($i = 0; $i -le $count; $i++) { Call $rec 'StringData' 'Get' @($i) }
                $detail = ($fields | Where-Object { $_ }) -join ' | '
            }
        } catch { }
        '{0,-58} FAIL  {1}' -f $q, $detail
    }
}
