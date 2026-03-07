# CLAUDE.md

Project instructions for AI assistants working in this repository.

## Project Overview

**OneClickInstaller** is an automated deployment system for Eikon kiosk workstations running Windows 10 LTSC. It installs 14+ software packages, configures MySQL, partitions disks, and performs a 19-step OS hardening/lockdown.

Two main applications work in sequence:
1. **OneClickInstaller** — WPF GUI that installs software, partitions disks, configures MySQL
2. **EikonConfigurator** — Console app that locks down Windows into a kiosk (accounts, AppLocker, UWF, WEKF, Defender, scheduled tasks, etc.)

## Architecture

| Project | Path | Framework | Build |
|---------|------|-----------|-------|
| OneClickInstaller | `src/OneClickInstaller/` | .NET Framework 4.8 (WPF) | MSBuild |
| EikonConfigurator | `src/EikonConfigurator/` | .NET 8.0 (win-x64, self-contained) | `dotnet publish` |
| EikonConfigurator.Tests | `tests/EikonConfigurator.Tests/` | .NET 9.0 (xUnit) | `dotnet test` |
| MockApps | `src/MockApps/` | .NET 8.0 (WinForms + WiX MSI) | `dotnet build` |

**Solution file:** `OneClickInstaller.sln` (root)

## Build Commands

```powershell
# Full build + assemble both deployment packages (Production + VM)
.\scripts\build\build_package.ps1

# Batch equivalent
.\scripts\build\build_all.bat

# EikonConfigurator + tests only
.\scripts\build\build_eikon.ps1

# Publish EikonConfigurator standalone
.\scripts\build\publish_oem.ps1

# Run tests
dotnet test tests\EikonConfigurator.Tests\EikonConfigurator.Tests.csproj
```

Build output goes to `deploy/output/` with two packages:
- `OneClickInstaller_Production/` — strict hardware checks for physical PCs
- `OneClickInstaller_VM/` — relaxed checks with `--bypass-hardware-check`

## Key Directories

```
config/                    → Configuration files
  installer-config.json    → Default/dev config (osConfiguration.enabled=false)
  deploy/                  → Production & VM config variants (4 JSON files)
  registry/                → Reference .reg files for kiosk lockdown
scripts/build/             → Build & packaging scripts
deploy/
  package-template/        → Template files shipped in every package (INSTALL.bat, README.txt)
  output/                  → Build output (gitignored)
  vm-files/                → VM testing artifacts
docs/                      → Requirements specs, flowcharts, service procedures
All_SW/                    → Third-party installer media (gitignored, ~7 GB)
```

## Configuration System

Two config files control behavior:
- **installer-config.json** — Controls OneClickInstaller: software packages, disk partitions, MySQL, and `osConfiguration` section (EikonConfigurator args)
- **hardware-config.json** — Controls EikonConfigurator: CPU/RAM/disk requirements, AppLocker paths, Defender exclusions, UWF/WEKF settings

Variants in `config/deploy/`:
- `installer-config.production.json` / `hardware-config.production.json` — strict hardware
- `installer-config.vm.json` / `hardware-config.vm.json` — relaxed for VMs

## EikonConfigurator CLI Arguments

```
--bypass-hardware-check    Skip CPU/RAM/disk/NIC validation (for VMs)
--log-path <path>          Override log file location
--dry-run                  Simulate without making changes
--admin-password <pass>    Set EikonAdmin password (default: EikonAdmin1!)
--resume                   Resume after reboot (set automatically via RunOnce)
```

## Code Conventions

- OneClickInstaller uses old-style .NET Framework csproj (non-SDK, `packages.config`)
- EikonConfigurator uses SDK-style csproj with `InternalsVisibleTo` for testing
- Both apps require Administrator privileges at runtime
- Config parsing in OneClickInstaller is manual JSON (no Newtonsoft/System.Text.Json — see `InstallerConfigModels.cs`)
- EikonConfigurator copies itself to `C:\EikonConfigurator\` before running (handles network/USB drives)
- Kiosk accounts: `EikonAdmin` (service), `EikonUser` (locked-down auto-logon)

## Important Notes

- Never run EikonConfigurator on a dev machine without `--dry-run` — it modifies system accounts, policies, and boot config
- The `All_SW/` folder contains ~7 GB of installer binaries and is gitignored
- MSBuild is required for OneClickInstaller (Visual Studio 2022 must be installed)
- Build scripts auto-detect VS edition (Community/Professional/Enterprise)
