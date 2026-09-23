# MsiMaker

Cross-platform MSI builder with a native desktop GUI, aimed at IT admins
deploying through Intune. Runs on Windows and macOS.

Built on its own pure-managed MSI engine (no WiX, no native dependencies), so
the same code path produces the same package on both operating systems.

See [docs/DESIGN.md](docs/DESIGN.md) for the format notes, the upgrade rules the
tool enforces, and current status.

## Layout

    src/MsiMaker.Core   engine: compound file, MSI tables, cabinet, Intune
    src/MsiMaker.Cli    headless build, for CI
    src/MsiMaker.App    Avalonia desktop GUI
    tools/              development-time verification helpers

## Verifying the engine

`tools/Verify-Msi.ps1` opens a built MSI through Windows Installer itself, which
is the real acceptance test. WiX 5 is used at development time only, as an
oracle to diff our output against; it ships with nothing.
