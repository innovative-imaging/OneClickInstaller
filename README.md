# OneClickInstaller

Automated deployment system for Eikon kiosk workstations. Installs 14+ software packages, configures MySQL, partitions disks, and performs a 19-step OS hardening/lockdown via EikonConfigurator.

## Repository Structure

```
OneClickInstaller/
├── OneClickInstaller.sln          ← Visual Studio solution (all projects)
├── src/
│   ├── OneClickInstaller/         ← WPF GUI installer (.NET Framework 4.8)
│   ├── EikonConfigurator/         ← OS kiosk lockdown tool (.NET 8, self-contained)
│   └── MockApps/                  ← Mock GVP-Pro application + services
├── tests/
│   └── EikonConfigurator.Tests/   ← xUnit tests for EikonConfigurator
├── docs/
│   ├── requirements/              ← Eikon Win10 OS Requirement Spec (.md + .html)
│   ├── flowchart/                 ← Installation flowchart
│   ├── references/                ← Software inventories, helper scripts
│   ├── ServiceLoginProcedure.*    ← Technician login procedure
│   └── analysis.md                ← Bug analysis notes
├── config/
│   ├── installer-config.json      ← Default installer configuration (dev)
│   ├── deploy/                    ← Production & VM config variants
│   └── registry/                  ← Reference .reg files
├── scripts/
│   └── build/                     ← Build & packaging scripts
├── deploy/
│   ├── package-template/          ← Deployment package template (INSTALL.bat, etc.)
│   ├── output/                    ← Build output (Production + VM packages)
│   └── vm-files/                  ← VM testing artifacts
└── All_SW/                        ← Third-party installer media (not in Git)
```

## Projects

| Project | Framework | Type | Purpose |
|---------|-----------|------|---------|
| **OneClickInstaller** | .NET Framework 4.8 | WPF | Main GUI — installs packages, partitions disks, configures MySQL |
| **EikonConfigurator** | .NET 8.0 (win-x64) | Console | 19-step kiosk OS hardening + seal/reseal |
| **EikonConfigurator.Tests** | .NET 9.0 | xUnit | Unit tests for EikonConfigurator |

## Prerequisites

- **Windows 10/11** (target OS)
- **Visual Studio 2022** (or MSBuild + .NET SDK 8.0)
- **.NET Framework 4.8 SDK** (for OneClickInstaller)
- **.NET 8.0 SDK** (for EikonConfigurator)
- **Administrator privileges** (required at runtime)

## Building

### Full Build (all projects + both packages)

```powershell
.\scripts\build\build_package.ps1
```

This produces two ready-to-deploy packages:
- `deploy\output\OneClickInstaller_Production\` — strict hardware checks for real PCs
- `deploy\output\OneClickInstaller_VM\` — relaxed checks with `--bypass-hardware-check --log-path`

### Individual Builds

```powershell
# OneClickInstaller + EikonConfigurator + both packages (batch version)
.\scripts\build\build_all.bat

# EikonConfigurator + Tests only
.\scripts\build\build_eikon.ps1

# Publish self-contained EikonConfigurator only
.\scripts\build\publish_oem.ps1
```

### Solution Build (Visual Studio)

Open `OneClickInstaller.sln` in Visual Studio 2022.

## Deployment

1. Run `.\scripts\build\build_package.ps1` to build and assemble both packages.
2. Choose the correct package from `deploy\output\`:
   - **OneClickInstaller_Production** — for physical Eikon kiosk PCs
   - **OneClickInstaller_VM** — for VM testing (bypasses hardware checks)
3. Copy the software installer media into the package's `SW\` folder.
4. Copy the complete package folder to the target machine (USB or network share).
5. On the target: right-click `INSTALL.bat` → **Run as administrator**.

The bootstrapper installs .NET Framework 3.5/4.8 (if needed), then launches OneClickInstaller, which installs all software packages and invokes EikonConfigurator for OS lockdown.

## Configuration

- **[config/installer-config.json](config/installer-config.json)** — default/dev configuration (OS lockdown disabled).
- **[config/deploy/](config/deploy/)** — production and VM config variants with hardware requirements.
- **hardware-config.json** (ships with EikonConfigurator) — CPU/RAM/disk requirements, AppLocker paths, Defender exclusions, UWF settings, WEKF filters.

## Documentation

- [Eikon Win10 OS Requirement Spec](docs/requirements/Eikon%20Win10%20OS%20Requirement%20Spec.md) — comprehensive technical specification
- [Installation Flowchart](docs/flowchart/installation_flowchart.html) — visual flow of the installation process
- [Service Login Procedure](docs/ServiceLoginProcedure.md) — technician maintenance access
