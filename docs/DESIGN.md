# MsiMaker — design

A native desktop app (Windows + macOS) for building MSI packages, aimed at
IT admins deploying through Intune. The design priority is **upgrade
correctness**: packages produced here must upgrade cleanly in Intune, every time.

## Decisions

### 1. Own MSI engine, not WiX

WiX ships `wixnative.exe` only for `win-x86`, `win-x64` and `win-arm64`. The
`wix` .NET tool *installs* on macOS but cannot emit an MSI there, because
database creation and cabinet compression are delegated to that native helper.
No WiX version fixes this.

Licensing closes the door further: WiX **5.0.2 is the last MS-RL release**.
6.0 and later moved to the Open Source Maintenance Fee model, which bills
companies above $10k revenue. Building on v5 means building on a frozen branch.

So `MsiMaker.Core` writes MSIs itself, in pure managed C# with no native
dependency:

```
Cfbf/     MS-CFB compound file reader + writer
Msi/      string pool, table streams, summary information, schema
Cab/      MSZIP cabinet writer
Payload/  PE parsing, installer fingerprinting
Build/    project model -> MSI
Intune/   upgrade analysis, .intunewin packaging
```

WiX 5 stays installed **as a development-time oracle only**. It builds reference
MSIs that our reader round-trips and our output is diffed against. It ships
with nothing.

### 2. Format facts were verified, not recalled

Every binary detail below was confirmed by decoding a real WiX-built MSI
(`tools/msidump.py`), not from memory:

- **Stream name encoding.** Names pack into the Unicode private-use area:
  pair `0x3800 + m1 + (m2 << 6)`, single `0x4800 + m`, table marker `0x4840`,
  over the alphabet `0-9A-Za-z._`. Verified by decoding every stream name.
- **String pool.** `_StringPool` is 4 bytes per string (`ushort length`,
  `ushort refcount`); entry 0 holds the codepage with `0x8000` signalling
  3-byte string references. `_StringData` is the bytes concatenated with no
  separators. Verified: parsing consumed exactly 6622/6622 bytes.
- **Rows are column-major.** A table stream holds every value of column 1,
  then every value of column 2, and so on — not row by row.
- **Integers carry a sign bias** so that a stored `0` can mean NULL:
  `+0x8000` for int16, `^0x80000000` for int32. This applies to the `Number`
  and `Type` columns of `_Columns` too, which is easy to miss.
- **Column type bits**: `0x0100` valid, `0x0800` string, `0x1000` nullable,
  `0x2000` key, low byte = size. String columns are `0x0D00 | len`.

The standard table schema in `Msi/MsiSchema.Generated.cs` is **harvested** from
a WiX MSI by `tools/genschema.py` rather than hand-written, so column types are
transcribed from a real database.

### 3. Acceptance test is Windows Installer, not our own reader

`tools/Verify-Msi.ps1` opens a built MSI through `msi.dll` via COM and dumps the
Property table and summary stream. A round-trip that satisfies our own reader
proves nothing; `msi.dll` accepting the file is the bar.

Current status: a WiX MSI read and fully rewritten by our engine is accepted by
Windows Installer with identical properties and summary information.

## The upgrade rules this tool enforces

Intune LOB MSI deployments detect on ProductCode alone, with no supersedence and
no custom detection. That makes the following non-negotiable, and the tool owns
them so the admin cannot get them wrong:

| Field | Rule | Failure if broken |
|---|---|---|
| `UpgradeCode` | Stable for the product's lifetime | Old version never removed; two entries in Programs & Features |
| `ProductCode` | **New GUID every version** | Intune sees "already installed", never upgrades |
| `ProductVersion` | Must increase in the **first three fields only** | `1.0.0.5` -> `1.0.0.6` is invisible to Windows Installer |
| Package code (summary 9) | New GUID every build | Caching and repair misbehave |
| `Template` (summary 7) | Platform must not change between versions | Upgrade silently does not match |
| `ALLUSERS=1` | Per-machine | Per-user MSI installed as SYSTEM lands in the wrong profile |
| `RemoveExistingProducts` | Scheduled at 1501, right after `InstallInitialize` (1500) | Orphaned files, or a failed rollback leaves nothing installed |

The `Upgrade` table shape WiX emits, and which we replicate:

```
{UPGRADECODE}  VersionMax=<this version>  Attr=513  -> WIX_UPGRADE_DETECTED
                                          513 = MigrateFeatures | VersionMaxInclusive
{UPGRADECODE}  VersionMin=<this version>  Attr=2    -> WIX_DOWNGRADE_DETECTED
                                          2   = OnlyDetect
```

**Upgrade doctor** compares a new build against the previously shipped MSI on
every one of these axes and states plainly whether Intune will upgrade in place,
reinstall, or silently do nothing.

## Wrapping EXE installers

Most payloads are not MSIs. HP Image Assistant, the first target, is a SoftPaq
self-extractor. The tool identifies the installer framework from the PE and
proposes silent switches rather than leaving the admin to guess:

| Fingerprint | Framework | Silent switches |
|---|---|---|
| `Inno Setup Setup Data` | Inno Setup | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` |
| `NullsoftInst` | NSIS | `/S /D=<dir>` |
| `.wixburn` PE section | WiX Burn bundle | `/quiet /norestart /log <file>` |
| `InstallShield` + `setup.ini` | InstallShield | `/s /v"/qn"` |
| `!@Install@!UTF-8!` | 7-Zip SFX | `-y -o<dir>` |
| HP SoftPaq / CVA sibling | HP SoftPaq | `/s /e /f <dir>` |

`VS_VERSIONINFO` in the PE also supplies ProductVersion and CompanyName, which
seed the MSI metadata and the upgrade version automatically.

Switches are a *proposal*, not a guess presented as fact: the UI offers a trial
run that reports exit code, wall time and whether the process stayed silent.

## Output stages

Building the `.msi` is the only mandatory stage. Everything else is opt-in:

- `.intunewin` packaging — written in-process (encrypted payload + `Detection.xml`),
  so it works on macOS with no `IntuneWinAppUtil.exe`. **Off by default.**
- Remediation script pair, for "run this thing periodically" packages like HPIA
  where what matters is configuration drift, not app presence. **Off by default.**

Detection scripts are deliberately **not** generated. Intune's Win32 MSI
detection rule reads the ProductCode and can version-compare; a PowerShell
detection script adds nothing and becomes another artefact to version-stamp.

## Bugs the end-to-end tests found

Every one of these produced a file our own reader accepted and Windows Installer
did not, so none would have been caught by round-tripping alone. They are the
argument for testing against msiexec with a real payload.

| Symptom | Cause |
|---|---|
| `1620` package could not be opened | Summary stream written as UTF-8. Windows Installer reads every string property back as empty, and a package with no Template is invalid. The database string pool may be UTF-8; the summary stream may not. |
| `2211` could not create table | `_Validation` absent and unlisted in `_Tables`. |
| `2229` could not load table | Rows must be stored in primary-key order, and for string keys that order is by string-pool index. Indices are reassigned on every write, so preserving source row order silently breaks the sort. |
| Tables load, then fail after an unrelated edit | String-pool refcounts were under-counted: `_Columns` names a table once per column, not once. |
| **Upgrade deletes 100 of 136 files** | `File.Version` left null. Windows Installer treats the file as unversioned, refuses to replace the versioned copy on disk (`Disallowing installation of component ... higher versioned keyfile exists`), then `RemoveExistingProducts` removes it. |
| Scheduled task never registers | Task XML shipped BOM-less UTF-16 (`Encoding.GetBytes` emits no preamble), so it decoded as UTF-8: "task XML is malformed ... one root element". |
| Custom action fails with error 1 | `[INSTALLFOLDER]` ends in a backslash, so `"...\"` escaped the closing quote. A trailing `.` keeps the argument well formed. |
| Task XML contains `[#fileId]` verbatim | MSI formats its own table fields but never the contents of a file it installs. |

## Failure and cleanup

Three things Windows Installer will not do on its own, which a package has to
arrange explicitly.

**Rollback.** A deferred custom action is not undone when a later step fails.
Without a paired rollback action, an install that dies after registering a
scheduled task or running a wrapped installer leaves both behind. Every deferred
action MsiMaker emits gets a rollback scheduled immediately ahead of it, since
rollback actions replay in reverse order. A run action's undo may be a different
program from the action itself - a wrapped installer is undone by the vendor's
uninstaller, not by running the installer again - so `RollbackCommand` is
separate from `RollbackArguments`. An action with no rollback at all produces a
build warning rather than silent risk.

Verified by forcing a failure: a package whose second task was deliberately
malformed failed at 1603, and the first task, which had already been registered,
was gone afterwards.

**Removing what a wrapped installer installed.** Uninstalling the MSI removes
the payload it laid down, not whatever an embedded installer wrote elsewhere. A
run action can therefore invoke an arbitrary shell command rather than only an
executable from the payload, which is how the vendor's own uninstaller gets
called. Verified end to end with 7-Zip: install lands 107 files in a directory
the MSI never created, and uninstall removes them.

**Files held open.** A running process blocks file replacement and deletion, and
Windows Installer answers that by failing or by quietly scheduling a reboot -
neither of which anyone sees on a silent Intune deployment. `CloseProcesses`
names the executables to close first.

The sequencing here is not free. RemoveExistingProducts must be the first thing
in the execution script, so nothing deferred may precede it: Windows Installer
rejects that outright with error 2613, "RemoveExistingProducts action sequenced
incorrectly". Closing therefore happens twice, and both passes are cheap - an
immediate action before InstallInitialize, which frees files before an upgrade
removes the old build, and a deferred one after RemoveExistingProducts, which
runs elevated and so still works when the immediate pass could not.

Verified with HP Image Assistant running and holding its own files open:
uninstall returned 0, the process was closed, and the folder, registry entries
and scheduled task were all removed with no reboot.

## Code signing

Signing is done in process, in managed code, so a package can be signed on macOS
with nothing installed. The alternative was making every macOS user run
`brew install osslsigncode` first, which is a dependency that exists only
because the signing was not ours.

The risk was real: the digest is a Windows-defined hash of the compound file
inside a PKCS#7 blob, and getting it subtly wrong yields a package that looks
signed and is rejected on the device. So it was written against an oracle -
digests produced by signtool and osslsigncode were pulled out of already-signed
packages and required to match byte for byte before a single signature was
produced. Four packages from two independent signers match.

Three things the obvious reading gets wrong, each found by that oracle:

- **Stream ordering is a plain memcmp over the NUL-terminated UTF-16 name
  bytes**, not the MS-CFB `(length, uppercase)` directory order. The two agree
  for most files and diverge precisely because every MSI has a name beginning
  ``.
- **A name may be a byte prefix of another** (`!File` is the start of
  `!FileSFPCatalog`), and the shorter sorts first - the comparison includes the
  terminator. No package on disk had such a pair, so one was built specifically
  to settle it.
- **The root CLSID is hashed last.** Omitting it gives a plausible-looking wrong
  answer.

Two further quirks are Windows-specific and were found only because a signature
osslsigncode happily accepted was rejected by Windows as "not a cryptographic
message": Authenticode does not wrap its eContent in an OCTET STRING, and
Windows refuses the SignedData version 3 that RFC 5652 requires for a non-data
content type. Both are patched after encoding, outside anything a signature
covers.

signtool and osslsigncode are still used for what only they can do - the Windows
certificate store, and Azure Trusted Signing.

A password is never a field in the project file, only the *name* of an
environment variable holding it - a project belongs in source control and a
secret in it would go too.

Three modes, inferred from whichever fields are filled in so older projects keep
building unchanged:

| Mode | Key material | Needs a tool | Verified here |
|---|---|---|---|
| `Pfx` | A PFX on disk, unlocked from an environment variable | **no** | Yes, end to end |
| `CertificateStore` | A certificate already in the Windows store, by thumbprint | signtool | Argument shape only |
| `AzureTrustedSigning` | The key never leaves the service; signtool calls it through a dlib | signtool | **No - see below** |

Verified for the PFX path with a throwaway self-signed certificate: the
signature embeds, carries an RFC 3161 timestamp, and the signed package still
loads every table and still installs and uninstalls - which also shows that
signtool's in-place edit is compatible with the compound file this tool writes.

Azure Trusted Signing is driven through signtool's dlib hook:

```
signtool sign /v /fd SHA256 /tr <timestamp> /td SHA256
              /dlib <Azure.CodeSigning.Dlib.dll> /dmdf <metadata.json> <file.msi>
```

The dlib ships only inside the `Microsoft.Trusted.Signing.Client` NuGet package,
so it is located in the restored package cache, newest version first. Credentials
come from the ambient Azure credential chain - `AZURE_TENANT_ID`,
`AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, or an interactive `az login` - and
never from the project file, for the same reason the PFX password is not a field.

**Not verified.** There is no Trusted Signing account reachable from here, so
nothing has been signed against the service. What is tested is the argument
construction, the metadata document, dlib discovery and every validation error.
The dlib's acceptance of that metadata and the credential chain itself both need
a first run against a real account.

## Scheduled tasks

An Intune platform script runs once and will not run again unless the script or
policy changes, so it cannot keep a fleet current on its own. Packages therefore
register a real scheduled task, removed again on uninstall.

The task is registered from XML rather than `schtasks` switches because the
settings that make a fleet-wide task safe have no switch equivalent: a random
delay so the estate does not all download at once, catch-up after a missed
start, battery and network conditions, and an execution time limit.

Because MSI never formats file contents, the XML ships with an install-directory
token that a small generated PowerShell registrar substitutes at install time.

## HP Image Assistant

HP's own Intune guidance packages HPIA as a Win32 app wrapping `Install.cmd`
(`hpia.exe /s /e /f c:\hpia`), detected by "the file `hpimageassistant.exe`
exists". That detection can never go from true to false, so **a new HPIA version
is never deployed** - the device already looks compliant. Uninstall is
`remove-item -recurse`.

Packaged as an MSI instead, the SoftPaq is extracted at build time and its files
shipped directly: 136 files, 27 MB of payload, a 10.9 MB package. Intune then
detects on ProductCode and version, so upgrades work, and uninstall is a real
uninstall. `samples/hpia-with-scheduled-task.json` also registers the weekly
analyse-and-install task using HP's documented command line.

## A project is a bundle

A bare `.json` is only half a package. It names payload files sitting somewhere
on the machine that wrote it, so it breaks the moment it is moved, shared, or
checked out elsewhere - and it broke exactly that way here: a folder payload
opened in the GUI looked empty, because relative paths were being resolved
against the process working directory rather than the project file's folder.

A `.msimaker` bundle is a zip carrying the project and every payload file:

```
project.json        the definition, payload paths rewritten bundle-relative
payload/...         every file, in the structure it installs with
```

Folder payloads are expanded when packing, so what is captured is exactly the
set of files the build installs, not a folder reference that may mean something
different in six months. That makes a package a single artefact to version, hand
over, or archive beside the MSI it produced.

HPIA packs from 136 files and 27 MB to a 10.5 MB bundle, and building that
bundle from an unrelated working directory produces the same ProductCode. The
package code still differs between those builds, because it identifies the
build rather than the product.

Unpacking refuses any entry whose path escapes the extraction folder, since a
zip may name one and unpacking blindly writes wherever the archive says.

## Recipe library

A built-in set of package definitions for applications that are awkward to
package, so the knowledge lives in the tool rather than in whoever last
researched the switches. `msimaker recipes` lists them; `msimaker recipe <id>
<out.json> <payload>` writes a starting project.

Each recipe carries an **Awkwardness** note explaining *why* the application is
hard, which is the part worth keeping: HPIA's file-exists detection that can
never go true to false, 7-Zip's NSIS `/D=` switch that must be last and
unquoted, Reader being a bootstrapper around an MSI that cannot be nested.

Only `hp-image-assistant` and `7zip` are marked **Verified**, because only those
two were actually built, installed, upgraded and uninstalled here. The other
seven carry the switches from vendor documentation and say so in their notes. A
confidently wrong silent switch breaks a fleet quietly, which is worse than a
note telling the admin to check.

Users extend the library by dropping JSON into `%APPDATA%\msimaker
ecipes`, or
by pointing `MSIMAKER_RECIPES` at a shared team folder. A user file reusing a
built-in id replaces it, so a wrong switch can be corrected the same day.

## Status

| Component | State |
|---|---|
| CFBF reader/writer | Round-trips a WiX MSI exactly |
| MSI tables, string pool, summary info | Accepted by `msi.dll`; builds install, upgrade and uninstall under msiexec |
| MSZIP cabinet writer | 27 MB payload to 10.9 MB, byte-exact on extraction |
| Harvested schema, sequences, `_Validation` | Generated from a real MSI by `tools/genschema.py` |
| Project model, folder payloads, builder | Working |
| Installer detective (PE fingerprints) | Working |
| Upgrade doctor | Working; verified against deliberately broken packages |
| Scheduled tasks | Registered on install, removed on uninstall |
| Rollback actions | Verified by forcing a failed install |
| Closing running processes | Verified with HPIA holding its own files open |
| Wrapped-EXE uninstall | Verified with 7-Zip's own uninstaller |
| Code signing (PFX) | Managed, no external tool; verified end to end, timestamped |
| Code signing (Azure Trusted Signing) | Implemented; **not** verified against a real account |
| `.intunewin` packaging | Working; HMAC verified |
| Recipe library | 9 recipes, 2 verified |
| Avalonia GUI | Builds and runs on Windows; cross-compiles for macOS |
| Project bundles | Verified portable; same ProductCode from any directory |
| Regression tests | 114 passing |
| macOS verification | Cross-compiles; not yet run on a Mac |
