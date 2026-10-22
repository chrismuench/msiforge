# MsiForge

Build MSI packages that upgrade cleanly in Intune — from Windows **or macOS**.

A native desktop app and a CLI, on top of an MSI engine written from scratch in
pure managed C#. No WiX, no native dependencies, nothing to install before it
works.

## Why this exists

Most failed Intune upgrades are not Intune's doing. They are a ProductCode that
did not change, a version that differs only in its fourth field, or a platform
that moved between builds — all of which are visible in the package before
anything is deployed. MsiForge owns those values so they cannot be got wrong,
and will tell you plainly whether Intune is going to upgrade, install alongside,
or quietly do nothing.

The other half is that you are packaging an application, not writing installer
code. There is no authoring project to learn and keep maintained — point it at a
folder, get an MSI, move on.

## Why the engine is our own

WiX ships `wixnative.exe` for `win-x86`, `win-x64` and `win-arm64` only. The
`wix` .NET tool installs on macOS but cannot emit an MSI there, because database
creation and cabinet compression are delegated to that native helper. That is
still the case in WiX v7, the current release.

Licensing is the other half. WiX source remains under MS-RL in every current
version, but from v6 onward *using* WiX carries a maintenance-fee obligation for
organisations above $10,000 in annual revenue. 5.0.2 is the last release on the
v5 branch, so staying off that fee means building on a branch that no longer
moves.

So the engine here is its own: MS-CFB compound file, MSI tables and string pool,
MSZIP cabinets, all in managed code. WiX 5 is used at development time only, as
an oracle to diff output against. It ships with nothing.

## Getting started

A release is **one file**. Download it and run it — there is no installer, and
no .NET runtime to install first.

The same binary is the desktop app and the command line. Run it with no
arguments and you get the window; run it with a command and you get the CLI:

```bash
MsiForge                      # the app
MsiForge doctor               # what can this machine do?
MsiForge selftest             # build and verify a package end to end
MsiForge build myapp.msiforge
```

On macOS the binary is not notarised, so clear the quarantine flag once:
`xattr -d com.apple.quarantine ./MsiForge-macos-arm64 && chmod +x ./MsiForge-macos-arm64`.

The Windows binaries are Authenticode-signed with a timestamp; the macOS and Linux
ones are not signed at all, since there is nothing equivalent to sign them with.

From source:

```bash
dotnet build MsiForge.slnx
dotnet run --project src/MsiForge.App -- doctor
dotnet run --project src/MsiForge.App              # the app
```

### The CLI

```
build      <blueprint|bundle> [out.msi] Build an MSI
pack       <blueprint.json> [out]       Bundle a blueprint and its payload into one file
analyze    <installer.exe> [proj.json]  Identify the installer and its silent switches
upgrade    <old.msi> <new.msi>          Will Intune actually upgrade? Check before shipping
intune     <file.msi>                   Exactly what to enter in the Intune portal
intunewin  <file.msi> [out]             Wrap an MSI as a Win32 .intunewin package
sign       <file.msi> [blueprint.json] Authenticode-sign a package
blueprints                              List the built-in package blueprints
blueprint  <id> <out.json> [payload]    Start a blueprint from a preloaded one
explain    <msiexec.log>                Say what actually went wrong in an install log
inspect    <file.msi>                   Show identity, upgrade and table summary
doctor                                  What this machine can and cannot do
selftest                                Build and verify a package end to end
```

## Parameters, and asking a package what it takes

A package that bakes every decision in at build time has to be rebuilt for every
variation of it. Declare the values instead, and they become ordinary MSI
properties an administrator can set on the command line:

```json
"Parameters": [
  { "Name": "INSTALLARGS", "Default": "/quiet /norestart",
    "Description": "Switches passed to the vendor installer." },
  { "Name": "LICENSEKEY", "Description": "Licence key. Leave empty for a trial." }
]
```

Reference one anywhere a command is built — `"Arguments": "[INSTALLARGS]"` — and
it resolves at install time:

```
msiexec /i app.msi /qn INSTALLARGS="/S /v/qn" LICENSEKEY=ABC-123
```

`INSTALLFOLDER` works the same way without being declared, so the install
location can be moved without a rebuild.

Two things are handled for you, because both fail silently otherwise. A property
name that is not entirely upper case is **ignored** by Windows Installer when it
appears on a command line — so a lower-case name is a build error here rather
than a parameter that quietly does nothing. And a public property is **dropped
when the install elevates** unless it is listed in `SecureCustomProperties`,
which every declared parameter is.

### `HELP=1`

Every package MsiForge builds can say what it accepts:

```
msiexec /i app.msi HELP=1
```

which shows a dialog listing every parameter, its description and its default,
then exits without installing anything. Under `/qn` there is nobody to show it
to, so the same text goes to the log.

It is a property rather than a switch because `msiexec` keeps `/?` and `/help`
for itself — a package never sees them.

## Where a package installs, and who can write there

`%ProgramFiles%` is the default, and it is the safe one: Windows grants ordinary
users read and execute there and nothing else.

By default the MSI carries a small **setup wizard** — Welcome, a destination-folder
page with Browse, a ready-to-install page, progress, and a completion screen — so
anyone running it by hand can choose where it goes and see it finish. It is
authored in pure MSI tables (no native custom actions) and a silent install
(`/qn`, as Intune uses) never shows it, so it costs nothing there. Turn it off
(`ShowInstallWizard: false`, or the checkbox on the Build step) for a package
meant only to be pushed silently. For a wrapped vendor installer whose directory
switch is recognised (NSIS `/D=`, Inno `/DIR=`, `INSTALLDIR=`, `APPDIR=`), the
chosen folder is passed through to the vendor installer — the app itself installs
there, not just the wrapper. The same happens for a command-line
`INSTALLFOLDER="C:\..."`. Under the covers this is the standard `CustomActionData`
plumbing, since a deferred action cannot read the property directly. An installer
whose switch MsiForge cannot recognise falls back to its own default.

`C:\ProgramData` is not the same. Its default ACL gives `BUILTIN\Users`
container-inherit **write and add-subdirectory**, and `CREATOR OWNER` full
control over anything they create — and every folder made underneath it inherits
that. So a custom action or a scheduled task running as SYSTEM out of a folder
there can be handed a DLL to load by any standard user on the machine. No race,
no pre-creation, no admin rights. (`C:\Windows\Temp` has the same weakness, for
the same reason — check with `icacls` before trusting a directory.)

A package that installs under ProgramData therefore gets its install folder
locked down: `MsiLockPermissionsEx` with a protected DACL, SYSTEM and
Administrators full, everyone else read and execute, inheritance broken. The
build says so in a warning. Set `"LockInstallFolder": false` if the application
genuinely needs to write into its own folder — and if it also runs something as
SYSTEM from there, the build will warn about that instead, because that is the
combination that hands out SYSTEM.

Scheduled tasks get the same treatment at the file level. A task that runs as
SYSTEM is stored under `System32\Tasks`, and the Task Scheduler service
already gives it a protected descriptor - but the file still inherits from its
folder, so the package breaks inheritance on the task it registered (that file
only, never the shared vendor folder) and fixes it to SYSTEM, Administrators and
read-only for everyone else.

## When a deployment fails

A failed install gives an administrator a 1603 and half a megabyte of log. The
cause is in there, but it is one line among thousands, phrased for whoever wrote
the installer engine.

```
msiforge explain install.log
```

Every explanation it gives is a failure that was hit, diagnosed and fixed while
building this tool, so it says what the message actually turned out to mean:

```
failing action: RemoveExistingProducts

[2613] RemoveExistingProducts is in the wrong place in the sequence.

    Nothing deferred may run between InstallInitialize and
    RemoveExistingProducts; it has to be the first thing in the execution script.
```

It recognises the quiet ones too - the missing `File.Version` that makes an
upgrade delete most of the application while every reader still accepts the
package.

## What works where

| | Windows | macOS / Linux |
|---|---|---|
| Build an MSI | yes | yes |
| Inspect, round-trip, upgrade check | yes | yes |
| `.intunewin` packaging | yes | yes |
| **Sign with a PFX** | **yes, nothing to install** | **yes, nothing to install** |
| Sign from the certificate store | yes | no — needs signtool |
| Azure Trusted Signing | yes (unverified) | no — needs signtool |
| Install and test what you built | yes | no — msiexec is Windows-only |

`doctor` reports all of this for the machine it runs on.

PFX signing is done in process, in managed code, so macOS needs no signer
installed. signtool and osslsigncode are still used where only they can help:
the Windows certificate store, and Azure Trusted Signing. If either is on the
machine it can be preferred explicitly.

The digest was written against an oracle — digests produced by signtool and
osslsigncode were pulled out of already-signed packages and required to match
byte for byte before a single signature was produced — because a subtly wrong
digest yields a package that looks signed and is rejected on the device.

## Blueprints and bundles

A blueprint is a `.json`. A **bundle** is a `.msiforge` — a zip carrying the
blueprint *and every payload file, including the original installer* — so it is
one artefact you can version, hand over, archive next to the MSI it produced, or
use to rebuild the exact same package on another machine. A bare `.json` names
payload files on the machine that wrote it, and breaks the moment it moves.

```bash
msiforge pack hpia.json HPIA.msiforge
msiforge build HPIA.msiforge
```

## The blueprint library

Some applications do not package the obvious way, and the knowledge of *why*
tends to live in whoever last researched it. `msiforge blueprints` lists built-in
blueprints that carry both the working configuration and the reason it is needed,
grouped into applications and device/driver tools.

Only `hp-image-assistant` is marked **verified** — it was actually built,
installed, upgraded and uninstalled here. The rest are marked **vendor docs**:
their switches come from the vendor's own documentation and simply have not been
run here, so give one a test install before a fleet rollout.

Add your own by dropping JSON into `%APPDATA%\msiforge\blueprints` (or wherever
`MSIFORGE_BLUEPRINTS` points). A file reusing a built-in id replaces it.

## Layout

```
src/MsiForge.Core   the engine: compound file, MSI tables, cabinet, blueprints, signing
src/MsiForge.Cli    the command line, linked into the app
src/MsiForge.App    Avalonia desktop GUI
tools/              development-time verification helpers
blueprints/            the built-in blueprint library (source folder)
```

## Testing

```bash
dotnet test tests/MsiForge.Tests/MsiForge.Tests.csproj
```

CI builds and tests on Windows, macOS and Linux, signs a package on macOS and
Linux with osslsigncode, and runs a full install / in-place upgrade / uninstall
against msiexec on Windows.

That last one matters: a structurally valid MSI can still be a broken package.
A missing `File.Version` once deleted 100 of 136 files during an upgrade, and
produced a database every reader accepted. Only msiexec finds that class of
fault.

`tools/Verify-Msi.ps1` opens a package through Windows Installer itself, which
is the real acceptance test — our own reader agreeing with our own writer proves
nothing.

The security analyzer rules (`CA2100`, `CA3xxx`, `CA5xxx`) are build errors, not
warnings. Everything else is left at its default: turning the whole analyzer set
to `All` produced 246 warnings, which is the same as none. The four suppressions
in the tree each carry the reason at the point of use — MD5 because the
`MsiFileHash` table is defined as MD5, SHA-1 because RFC 4122 defines a version 5
UUID that way, and so on.

Packages, bundles and logs are all read as untrusted input: `inspect` on a
stranger's MSI, or opening a bundle someone sent, should not be able to hang the
process, exhaust memory, or run anything. The parsers are hand-written, so that
is tested rather than assumed.

See [docs/DESIGN.md](docs/DESIGN.md) for the format notes, the upgrade rules the
tool enforces, and the bugs that were found the hard way, and
[docs/RELEASE-SIGNING.md](docs/RELEASE-SIGNING.md) for how the release binaries
get signed from CI without a certificate file being stored anywhere.

## License

This project is licensed under the MIT License - see the [LICENSE.txt](LICENSE.txt) file for details.
