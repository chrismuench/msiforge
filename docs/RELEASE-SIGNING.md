# Signing a release

The two Windows binaries in every release are Authenticode-signed by Azure Artifact
Signing (the service formerly called Azure Trusted Signing). The macOS and Linux
binaries are not signed — Authenticode has no equivalent there, and macOS signing
plus notarisation is a separate job with an Apple Developer ID in it.

Authentication is OIDC. There is no certificate file, no PFX, no password and no
expiry to babysit: the runner exchanges its own identity for a short-lived token,
uses it to sign, and the token dies with the job. Nothing in this repository can
be stolen and used to sign anything.

## One-time setup, Azure side

1. **Signing account.** Note its region. The endpoint has to match it — a
   region/endpoint mismatch shows up as `403 Forbidden` with an internal
   `SignerSign()` failure, which reads like a permissions problem and is not one.

   ```
   https://eus.codesigning.azure.net     West Europe      (example)
   https://neu.codesigning.azure.net     North Europe
   https://wus2.codesigning.azure.net    West US 2
   ```

2. **Certificate profile** in that account.

3. **An Entra app registration to act as the pipeline.** Create a *server
   application* with no secret, then add a federated credential:

   | Field | Value |
   |---|---|
   | Issuer | `https://token.actions.githubusercontent.com` |
   | Subject | `repo:chrismuench/msiforge:environment:release` |
   | Granted token audience | `api://AzureADTokenExchange` |

   Scoping the subject to the `release` environment rather than to a branch or to
   `*` means only runs of that environment can mint a usable token. A wildcard, or
   a subject pinned to a branch, is worse: releases are triggered by **tags**, so a
   `ref:refs/heads/main`-style subject will not match a tag push at all.

4. **Grant the app permission to sign**, on the signing account or its resource
   group: the built-in role is **Artifact Signing Certificate Profile Signer**.

5. Note the **tenant ID**, **application (client) ID** and the **subscription ID**
   that holds the account.

## One-time setup, GitHub side

Create the environment first: *Settings → Environments → release*. Its secrets and
variables are what the workflow reads; repo-level ones work too, since `secrets`
and `vars` resolve both scopes.

| Kind | Name | Example / note |
|---|---|---|
| secret | `AZURE_TENANT_ID` | tenant of the app registration |
| secret | `AZURE_CLIENT_ID` | application (client) ID |
| secret | `AZURE_SUBSCRIPTION_ID` | subscription holding the account |
| variable | `AZURE_SIGNING_ENDPOINT` | `https://<region>.codesigning.azure.net` |
| variable | `AZURE_SIGNING_ACCOUNT` | signing account name |
| variable | `AZURE_CERTIFICATE_PROFILE` | certificate profile name |

The last three are not secret — anyone with read access to the repo can already
learn them from a signed binary's certificate — so they are variables, which means
they show up in the Settings UI instead of being an opaque `••••••` you cannot
proofread.

The client ID, tenant ID and subscription ID are identifiers rather than
credentials, so putting them in secrets is convention more than necessity. Nothing
in this workflow would weaken if they were variables instead.

## Doing it from PowerShell

`tools/New-SigningOidcCredential.ps1` carries out steps 3 to 5 for any number of
repositories, and is safe to re-run — existing apps, credentials and role
assignments are found and left alone.

```powershell
./tools/New-SigningOidcCredential.ps1 `
    -TenantId '...' -SubscriptionId '...' `
    -AccountName 'contoso-signing' -ProfileName 'contoso-release' `
    -Region 'wus3' `
    -Repos 'me/msiforge','me/other-app','me/third-app','me/fourth-app'
```

It checks `-Region` against where the signing account actually lives and overrides
it with a warning if they disagree, since that mismatch is otherwise a 403 at the
first signing call rather than an error here. Then it prints the `gh variable` and
`gh secret` commands to finish each repository.

Three things that cost time if you script this by hand:

- `New-AzADAppFederatedCredential` takes **`-ApplicationObjectId`** — the app's
  object id, not the client id you will be pasting into GitHub.
- The service principal appears a moment *after* the application does. Assigning a
  role before it lands fails with something that reads like a permission problem;
  the script polls for it.
- The subject has to say `environment:`, not a branch. See step 3.

## What the release job does

`build` (all five platforms) → publish → smoke-test the binary → name the asset →
check signing is configured → `azure/login` → sign → **verify** → upload.
`publish` then opens the release from the uploaded artifacts.

Signing happens after the smoke test and after the rename, so the bytes verified
are the bytes uploaded.

Verification is not decoration:

- `Get-AuthenticodeSignature` must report `Valid`, or the job throws.
- `signtool verify /pa /v` must exit 0.
- A missing configuration value fails the job with a named, itemised error rather
  than quietly shipping unsigned — an unsigned binary that used to be signed is the
  kind of regression nobody notices until SmartScreen says so on a customer's
  machine.
- The signature must carry an RFC 3161 timestamp
  (`http://timestamp.acs.microsoft.com`), which is what keeps it valid after the
  certificate expires.

## Rehearsing

Run *Actions → release → Run workflow* (`workflow_dispatch`). It takes the same
path as a tag push, including signing and verification, and stops before opening a
release. Nothing needs tagging to test the whole thing.

## Known limits

- **SmartScreen reputation is not bought by signing.** A certificate profile that
  has just started signing has no history, so the first downloads may still warn.
  Reputation accrues with clean install volume, not with the certificate itself.
- **Nothing here signs your packages.** `msiforge sign` reaches the same service
  through `Microsoft.Trusted.Signing.Client` and its own
  `AzureEndpoint` / `AzureCodeSigningAccountName` / `AzureCertificateProfileName`
  options, on a machine you control. This document is about the binary you ship,
  not the MSIs that binary produces.
- **The macOS binaries are notarised by nobody.** Until an Apple Developer ID
  exists, the release notes tell people to clear the quarantine attribute, which is
  honest but is not the same as Gatekeeper accepting the file.

## Where the names came from

The role name, endpoint pattern, integration list and the 403/`SignerSign()`
warning were taken from the Artifact Signing documentation
(`how-to-signing-integrations`, `concept-resources-roles`), and the action inputs
and OIDC requirements from `azure/artifact-signing-action@v2` and its
`docs/OIDC.md`. The one thing worth checking against your own tenant is the
federation subject: if a run fails with an `AADSTS` error about an unrecognised
subject, that error prints the exact subject the token asserted. Copy that into the
credential instead of guessing at the format.
