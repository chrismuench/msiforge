<#
.SYNOPSIS
  Creates the Entra app registrations and GitHub Actions federated credentials
  that let a repository sign binaries with Azure Artifact Signing, without a
  certificate file or a client secret anywhere.

.DESCRIPTION
  Repeatable: run it as often as you like. Existing apps, credentials and role
  assignments are found and left alone, and only the missing pieces are created.

  One federated credential per repository/environment pair, each pinned to
  repo:<owner>/<repo>:environment:<env>. The subject is what does the security
  work here - a token minted by any other workflow, branch or repository will not
  exchange, so there is no need to lock down a secret.

  Pinning to an environment rather than to a branch ref is deliberate for release
  signing: these workflows fire on version tags, and a subject of the form
  ref:refs/heads/main never matches a tag push.

  Prints the exact `gh` commands to finish the GitHub side, because the values are
  only useful once they are in the right repository's settings.

.EXAMPLE
  ./tools/New-SigningOidcCredential.ps1 -TenantId '...' -SubscriptionId '...' `
      -AccountName 'contoso-signing' -ProfileName 'contoso-release'

.NOTES
  Needs write access to Entra (Application developer or cloud application admin)
  and permission to assign roles on the signing account. The Artifact Signing
  account must already exist, with a certificate profile in it - creating those is
  a portal or ARM job, and both have identity validation steps a script cannot
  complete on your behalf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$SubscriptionId,

    # The Artifact Signing account name, as shown in the portal.
    [Parameter(Mandatory)][string]$AccountName,

    # Certificate profile inside that account.
    [Parameter(Mandatory)][string]$ProfileName,

    # Endpoint short code. wus3 = West US 3. The script checks this against the
    # account's actual location and says so if they disagree, because a mismatched
    # region and endpoint fails as "403 Forbidden" plus an internal SignerSign()
    # error - which looks exactly like a permissions problem and is not one.
    [string]$Region = 'wus3',

    # owner/repo pairs that will sign. Environment must match the job's
    # `environment:` in the workflow.
    [string[]]$Repos = @(
        'chrismuench/msiforge'
    ),
    [string]$Environment = 'release',

    # Built-in role that grants signing rights. Confirm with
    #   Get-AzRoleDefinition -Name '*Signing*'
    # if this ever stops resolving - the product was renamed from Azure Trusted
    # Signing, and the role name is the one thing in here that could drift.
    [string]$RoleName = 'Artifact Signing Certificate Profile Signer',

    # A shared app registration holds one credential per repo. Per-repo apps cost
    # nothing but give each repository its own identity to revoke, so it is on by
    # default.
    [switch]$SharedApp,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Issuer   = 'https://token.actions.githubusercontent.com'
$Audience = 'api://AzureADTokenExchange'

# Azure region display name / location token -> the short code the endpoint uses.
# Only the codes the service actually publishes; see how-to-signing-integrations.
$RegionCode = @{
    eastus = 'eus'; westus = 'wus'; westus2 = 'wus2'; westus3 = 'wus3'
    westcentralus = 'wcus'; centralus = 'cus'; northcentralus = 'ncus'
    southcentralus = 'scus'; brazilsouth = 'brs'; westeurope = 'weu'
    northeurope = 'neu'; japaneast = 'jpe'; koreacentral = 'krc'
    polandcentral = 'plc'; swedencentral = 'swn'
}

function Step($msg) { Write-Host "  $msg" -ForegroundColor DarkGray }
function Done($msg) { Write-Host "  $msg" -ForegroundColor Green }
function Newer($msg) { Write-Host "  $msg" -ForegroundColor Yellow }

Connect-AzAccount -TenantId $TenantId -SubscriptionId $SubscriptionId | Out-Null
Select-AzSubscription -SubscriptionId $SubscriptionId | Out-Null

# ---------------------------------------------------------------- the account
$acct = Get-AzResource -ResourceType 'Microsoft.CodeSigning/codeSigningAccounts' |
        Where-Object Name -eq $AccountName | Select-Object -First 1
if (-not $acct) {
    $seen = (Get-AzResource -ResourceType 'Microsoft.CodeSigning/codeSigningAccounts' |
             ForEach-Object Name) -join ', '
    throw "signing account '$AccountName' not found in subscription $SubscriptionId." +
          ($(if ($seen) { " Found: $seen" } else { " Found none - create the account first." }))
}

$actualCode = $RegionCode[$acct.Location.Replace(' ', '').ToLowerInvariant()]
if (-not $actualCode) {
    Newer "location '$($acct.Location)' is not in the endpoint table; check the published list"
}
elseif ($actualCode -ne $Region) {
    Newer ("region mismatch: -Region says {0}, but the account lives in '{1}' ({2})." -f `
            $Region, $acct.Location, $actualCode)
    Newer ("this would fail every signing call with 403 / SignerSign(); using {0}" -f $actualCode)
    $Region = $actualCode
}
$Endpoint = "https://$Region.codesigning.azure.net"
Done "signing account : $($acct.ResourceId)"
Done "endpoint        : $Endpoint"
Write-Host ''

$results = @()

foreach ($repo in $Repos) {
    $short = ($repo -split '/')[-1]
    $appName = if ($SharedApp) { "oidc-artifact-signing" } else { "oidc-artifact-signing-$short" }
    Write-Host "$repo" -ForegroundColor Cyan

    # ------------------------------------------------------ app registration
    # Filter is OData and the value is single-quoted; a display name with an
    # apostrophe would need escaping, which repo names do not produce.
    $app = Get-AzADApplication -Filter "displayName eq '$appName'" | Select-Object -First 1
    if ($app) {
        Step "app '$appName' exists ($($app.AppId))"
    }
    else {
        if ($PSCmdlet.ShouldProcess($appName, 'create app registration')) {
            # SignInAudience AzureADMyOrg keeps it single-tenant: only this tenant's
            # identities, and GitHub's issuer, can ever get a token from it.
            $app = New-AzADApplication -DisplayName $appName -SignInAudience 'AzureADMyOrg'
            Done "created app '$appName' (clientId $($app.AppId))"
        }
        else {
            # -WhatIf: there is no object id to work with, so continuing would mean
            # reading properties off $null and failing at the service principal.
            Newer "would create app '$appName'; nothing else to report for $repo"
            continue
        }
    }
    if ($DryRun) { continue }

    # Entra creates the service principal asynchronously after the app is made,
    # and a role assignment against a principal that has not landed yet fails with
    # a message that reads like a permissions error. Retry rather than guess.
    $sp = $null
    for ($i = 1; $i -le 12 -and -not $sp; $i++) {
        $sp = Get-AzADServicePrincipal -ApplicationId $app.AppId -ErrorAction SilentlyContinue
        if (-not $sp) { Step "waiting for the service principal ($i/12)"; Start-Sleep -Seconds 5 }
    }
    if (-not $sp) {
        $sp = New-AzADServicePrincipal -ApplicationId $app.AppId
        Done "created service principal"
    }
    else { Step "service principal exists" }

    # --------------------------------------------------- federated credential
    # Note -ApplicationObjectId: this is the app's OBJECT id ($app.Id), not the
    # client id. Passing AppId here fails with a lookup error that invites you to
    # paste the wrong value again.
    $subject = "repo:${repo}:environment:${Environment}"
    $credName = $repo -replace '/', '-'
    $existing = @(Get-AzADAppFederatedCredential -ApplicationObjectId $app.Id -ErrorAction Stop) |
                Where-Object Subject -eq $subject
    if ($existing) {
        Step "credential already trusts $subject"
    }
    elseif ($PSCmdlet.ShouldProcess($subject, 'create federated credential')) {
        New-AzADAppFederatedCredential `
            -ApplicationObjectId $app.Id `
            -Name $credName `
            -Issuer $Issuer `
            -Audience $Audience `
            -Subject $subject | Out-Null
        Done "trusted $subject"
    }

    # --------------------------------------------------------- role assignment
    $ra = Get-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName $RoleName `
            -Scope $acct.ResourceId -ErrorAction SilentlyContinue
    if ($ra) {
        Step "already has '$RoleName' on the account"
    }
    elseif ($PSCmdlet.ShouldProcess($acct.ResourceId, "assign $RoleName")) {
        New-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName $RoleName `
            -Scope $acct.ResourceId -ErrorAction Stop | Out-Null
        Done "assigned '$RoleName'"
    }

    $results += [pscustomobject]@{
        Repo = $repo; ClientId = $app.AppId; Endpoint = $Endpoint
        Account = $AccountName; Profile = $ProfileName; Subject = $subject
    }
    Write-Host ''
}

if (-not $results) { Write-Host 'Dry run - nothing to report.'; return }

# ------------------------------------------------------------- what is left
Write-Host 'Remaining work is in GitHub, per repository:' -ForegroundColor Cyan
foreach ($r in $results) {
    Write-Host "  # $($r.Repo)" -ForegroundColor DarkGray
    Write-Host "  gh variable set AZURE_SIGNING_ENDPOINT -b `"$($r.Endpoint)`" -R $($r.Repo)"
    Write-Host "  gh variable set AZURE_SIGNING_ACCOUNT -b `"$($r.Account)`" -R $($r.Repo)"
    Write-Host "  gh variable set AZURE_CERTIFICATE_PROFILE -b `"$($r.Profile)`" -R $($r.Repo)"
    Write-Host "  echo `"$TenantId`" | gh secret set AZURE_TENANT_ID -R $($r.Repo)"
    Write-Host "  echo `"$($r.ClientId)`" | gh secret set AZURE_CLIENT_ID -R $($r.Repo)"
    Write-Host "  echo `"$SubscriptionId`" | gh secret set AZURE_SUBSCRIPTION_ID -R $($r.Repo)"
    Write-Host ''
}
Write-Host 'Also, once, per repository: create the GitHub environment named'
Write-Host "'$Environment' (Settings -> Environments). The federated credential's subject"
Write-Host 'only matches tokens minted for that environment, so until it exists the'
Write-Host 'exchange fails. GitHub creates it on first use too, but naming it yourself'
Write-Host 'is where the approval gate and secret scoping live.'
