# MsiMaker

Build MSI packages that upgrade cleanly in Intune — from Windows **or macOS**.

A native desktop app and a CLI, on top of an MSI engine written from scratch in
pure managed C#. No WiX, no native dependencies, nothing to install before it
works.

## Why this exists

Most failed Intune upgrades are not Intune's doing. They are a ProductCode that
did not change, a version that differs only in its fourth field, or a platform
that moved between builds — all of which are visible in the package before
anything is deployed. MsiMaker owns those values so they cannot be got wrong,
and will tell you plainly whether Intune is going to upgrade, install alongside,
or quietly do nothing.

## Why not WiX

WiX ships `wixnative.exe` for `win-x86`, `win-x64` and `win-arm64` only. The
`wix` .NET tool installs on macOS but cannot emit an MSI there, because database
creation and cabinet compression are delegated to that native helper. No version
of WiX fixes this.

Licensing closes the door further: **WiX 5.0.2 is the last MS-RL release**. 6.0
and later moved to the Open Source Maintenance Fee model, which bills companies
above $10k revenue.

So the engine here is its own: MS-CFB compound file, MSI tables and string pool,
MSZIP cabinets, all in managed code. WiX 5 is used at development time only, as
an oracle to diff output against. It ships with nothing.

## Getting started

```bash
dotnet build MsiMaker.slnx

# what can this machine do?
dotnet run --project src/MsiMaker.Cli -- doctor

# build and verify a package end to end
dotnet run --project src/MsiMaker.Cli -- selftest
```

The desktop app:

```bash
dotnet run --project src/MsiMaker.App
```

### The CLI

```
build      <project|bundle> [out.msi]   Build an MSI
pack       <project.json> [out]         Bundle a project and its payload into one file
analyze    <installer.exe> [proj.json]  Identify the installer and its silent switches
upgrade    <old.msi> <new.msi>          Will Intune actually upgrade? Check before shipping
intune     <file.msi>                   Exactly what to enter in the Intune portal
intunewin  <file.msi> [out]             Wrap an MSI as a Win32 .intunewin package
sign       <file.msi> [project.json]    Authenticode-sign a package
recipes                                 List the built-in package recipes
recipe     <id> <out.json> [payload]    Start a project from a recipe
explain    <msiexec.log>                Say what actually went wrong in an install log
inspect    <file.msi>                   Show identity, upgrade and table summary
doctor                                  What this machine can and cannot do
selftest                                Build and verify a package end to end
```

## When a deployment fails

A failed install gives an administrator a 1603 and half a megabyte of log. The
cause is in there, but it is one line among thousands, phrased for whoever wrote
the installer engine.

```
msimaker explain install.log
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
| Sign with a PFX | signtool or osslsigncode | osslsigncode |
| Sign from the certificate store | yes | no — needs signtool |
| Azure Trusted Signing | yes (unverified) | no — needs signtool |
| Install and test what you built | yes | no — msiexec is Windows-only |

`doctor` reports all of this for the machine it runs on.

For signing off Windows, install the signer first:

```bash
brew install osslsigncode          # macOS
sudo apt install osslsigncode      # Debian/Ubuntu
```

## Projects and bundles

A project is a `.json`. A **bundle** is a `.msimaker` — a zip carrying the
project *and every payload file*, so a package is one artefact you can version,
hand over, or archive next to the MSI it produced. A bare `.json` names payload
files on the machine that wrote it, and breaks the moment it moves.

```bash
msimaker pack hpia.json HPIA.msimaker
msimaker build HPIA.msimaker
```

## The recipe library

Some applications do not package the obvious way, and the knowledge of *why*
tends to live in whoever last researched it. `msimaker recipes` lists built-in
recipes that carry both the working configuration and the reason it is needed.

Only `hp-image-assistant` and `7zip` are marked **verified** — those two were
actually built, installed, upgraded and uninstalled here. The rest carry
vendor-documented switches and say so, because a wrong silent switch fails
quietly across a fleet.

Add your own by dropping JSON into `%APPDATA%\msimaker\recipes` (or wherever
`MSIMAKER_RECIPES` points). A file reusing a built-in id replaces it.

## Layout

```
src/MsiMaker.Core   the engine: compound file, MSI tables, cabinet, recipes, signing
src/MsiMaker.Cli    headless build, for CI
src/MsiMaker.App    Avalonia desktop GUI
tools/              development-time verification helpers
recipes/            the built-in recipe library
```

## Testing

```bash
dotnet test tests/MsiMaker.Tests/MsiMaker.Tests.csproj
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

See [docs/DESIGN.md](docs/DESIGN.md) for the format notes, the upgrade rules the
tool enforces, and the bugs that were found the hard way.
