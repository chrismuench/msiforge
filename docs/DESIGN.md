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
Intune/   upgrade analysis, .intunewin, Graph upload
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
- Graph API upload to the tenant, including wiring supersedence to the previous
  version. **Off by default.**
- Remediation script pair, for "run this thing periodically" packages like HPIA
  where what matters is configuration drift, not app presence. **Off by default.**

Detection scripts are deliberately **not** generated. Intune's Win32 MSI
detection rule reads the ProductCode and can version-compare; a PowerShell
detection script adds nothing and becomes another artefact to version-stamp.

## Status

| Component | State |
|---|---|
| CFBF reader/writer | Working; round-trips a WiX MSI exactly |
| MSI string pool, tables, summary info | Working; accepted by `msi.dll` |
| Harvested standard schema (25 tables) | Done |
| CLI `inspect` / `roundtrip` | Working |
| MSZIP cabinet writer | Not started |
| Project model -> MSI build | Not started |
| Upgrade doctor | Not started |
| Installer detective (PE) | Not started |
| Avalonia GUI | Not started |
| `.intunewin` / Graph upload | Not started |
