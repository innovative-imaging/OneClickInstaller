#Requires -Version 5.1
<#
.SYNOPSIS
    Builds OneClickInstaller + EikonConfigurator and assembles TWO deployment packages
    (Production and VM).

.DESCRIPTION
    This script:
    1. Builds the OneClickInstaller WPF app (.NET Framework 4.8)
    2. Builds the EikonConfigurator (.NET 8.0, self-contained win-x64)
    3. Assembles two ready-to-deploy packages:
       - deploy\output\OneClickInstaller_Production\  (strict hardware checks)
       - deploy\output\OneClickInstaller_VM\          (relaxed checks, --bypass-hardware-check)

    Each package is self-contained: copy to USB/network share and run INSTALL.bat.

.EXAMPLE
    .\build_package.ps1
    .\build_package.ps1 -SkipBuild   # Only assemble packages (skip compilation)
#>

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
# Resolve repository root (this script is in scripts/build/)
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\.." )).Path

# --- Paths ---
$MSBuild = $null
foreach ($edition in @("Community", "Professional", "Enterprise")) {
    $candidate = "C:\Program Files\Microsoft Visual Studio\2022\$edition\MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path $candidate) { $MSBuild = $candidate; break }
}
if (-not $MSBuild) { Write-Error "MSBuild not found. Install Visual Studio 2022." }

$OneClickCsproj  = Join-Path $RepoRoot "src\OneClickInstaller\OneClickInstaller.csproj"
$EikonCsproj     = Join-Path $RepoRoot "src\EikonConfigurator\EikonConfigurator.csproj"
$EikonPublishDir = Join-Path $RepoRoot "src\EikonConfigurator\bin\publish"
$BinRelease      = Join-Path $RepoRoot "src\OneClickInstaller\bin\Release"
$OutputRoot      = Join-Path $RepoRoot "deploy\output"
$TemplateDir     = Join-Path $RepoRoot "deploy\package-template"
$ConfigDeployDir = Join-Path $RepoRoot "config\deploy"
$ConfigBaseFile  = Join-Path $RepoRoot "config\installer-config.base.json"

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  OneClickInstaller - Package Builder (Production + VM)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# Step 1: Build OneClickInstaller (WPF/.NET Framework 4.8)
# ============================================================
if (-not $SkipBuild) {
    Write-Host "[1/4] Building OneClickInstaller..." -ForegroundColor Yellow

    & $MSBuild $OneClickCsproj /p:Configuration=Release /t:Build /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { Write-Error "OneClickInstaller build failed (exit code $LASTEXITCODE)" }

    Write-Host "  OneClickInstaller built successfully." -ForegroundColor Green
    Write-Host ""

    # ============================================================
    # Step 2: Build EikonConfigurator (.NET 8.0, self-contained)
    # ============================================================
    Write-Host "[2/4] Building EikonConfigurator..." -ForegroundColor Yellow

    dotnet publish $EikonCsproj -c Release -r win-x64 --self-contained -o $EikonPublishDir /p:PublishSingleFile=false 2>&1 | Out-String | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { Write-Error "EikonConfigurator build failed (exit code $LASTEXITCODE)" }

    Write-Host "  EikonConfigurator built successfully." -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "[1-2/4] Skipping builds (using existing artifacts)..." -ForegroundColor DarkGray
    Write-Host ""
}

# ============================================================
# Helper: Assemble a single package variant
# ============================================================
function New-Package {
    param(
        [string]$PackageDir,
        [string]$Mode,        # "production" or "vm"
        [string]$Label        # Display label
    )

    Write-Host "  --- $Label ---" -ForegroundColor White

    # Clean and create output directory
    if (Test-Path $PackageDir) { Remove-Item $PackageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

    # Copy template files (INSTALL.bat, UNINSTALL_DOTNET48.bat, README.txt, .exe.config)
    Get-ChildItem $TemplateDir -File | Copy-Item -Destination $PackageDir -Force
    Write-Host "    Copied template files (INSTALL.bat, README.txt, etc.)"

    # Copy OneClickInstaller.exe
    $oneClickExe = Join-Path $BinRelease "OneClickInstaller.exe"
    if (-not (Test-Path $oneClickExe)) { Write-Error "OneClickInstaller.exe not found at: $oneClickExe" }
    Copy-Item $oneClickExe $PackageDir -Force
    Write-Host "    Copied: OneClickInstaller.exe"

    # Merge installer-config.base.json + variant overrides -> installer-config.json
    $configSrc = Join-Path $ConfigDeployDir "installer-config.$Mode.json"
    $configDst = Join-Path $PackageDir "installer-config.json"
    if (-not (Test-Path $configSrc))       { Write-Error "Config variant not found: $configSrc" }
    if (-not (Test-Path $ConfigBaseFile))  { Write-Error "Base config not found: $ConfigBaseFile" }

    $base    = Get-Content $ConfigBaseFile -Raw | ConvertFrom-Json
    $variant = Get-Content $configSrc     -Raw | ConvertFrom-Json
    foreach ($prop in $variant.PSObject.Properties) {
        $base | Add-Member -MemberType NoteProperty -Name $prop.Name -Value $prop.Value -Force
    }
    $base | ConvertTo-Json -Depth 20 | Set-Content $configDst -Encoding UTF8
    Write-Host "    Merged: installer-config.base.json + installer-config.$Mode.json -> installer-config.json"

    # Copy EikonConfigurator folder
    $eikonDest = Join-Path $PackageDir "EikonConfigurator"
    if (-not (Test-Path $EikonPublishDir)) { Write-Error "EikonConfigurator publish output not found at: $EikonPublishDir" }
    Copy-Item $EikonPublishDir $eikonDest -Recurse -Force
    Write-Host "    Copied: EikonConfigurator/ ($(( Get-ChildItem $eikonDest -Recurse -File).Count) files)"

    # Copy the correct hardware-config variant into EikonConfigurator/
    $hwSrc = Join-Path $ConfigDeployDir "hardware-config.$Mode.json"
    $hwDst = Join-Path $eikonDest "hardware-config.json"
    if (Test-Path $hwSrc) {
        Copy-Item $hwSrc $hwDst -Force
        Write-Host "    Copied: hardware-config.$Mode.json -> EikonConfigurator/hardware-config.json"
    }

    # Copy KioskLauncher.vbs into EikonConfigurator/ (required by hardware-config.json startupFiles)
    $kioskVbsSrc = Join-Path $RepoRoot "src\KioskLauncher.vbs"
    if (-not (Test-Path $kioskVbsSrc)) { Write-Error "KioskLauncher.vbs not found at: $kioskVbsSrc" }
    Copy-Item $kioskVbsSrc $eikonDest -Force
    Write-Host "    Copied: KioskLauncher.vbs -> EikonConfigurator/"

    # Create empty SW/ directory with a placeholder note
    $swDir = Join-Path $PackageDir "SW"
    New-Item -ItemType Directory -Path $swDir -Force | Out-Null
    Set-Content (Join-Path $swDir "_COPY_INSTALLERS_HERE.txt") "Copy the software installer media into this SW\ folder before deployment."
    Write-Host "    Created: SW/ (copy installer media here before deployment)"
}

# ============================================================
# Step 3: Assemble Production package
# ============================================================
Write-Host "[3/4] Assembling Production package..." -ForegroundColor Yellow
$ProdDir = Join-Path $OutputRoot "OneClickInstaller_Production"
New-Package -PackageDir $ProdDir -Mode "production" -Label "PRODUCTION (strict hardware checks)"
Write-Host ""

# ============================================================
# Step 4: Assemble VM package
# ============================================================
Write-Host "[4/4] Assembling VM package..." -ForegroundColor Yellow
$VmDir = Join-Path $OutputRoot "OneClickInstaller_VM"
New-Package -PackageDir $VmDir -Mode "vm" -Label "VM / TEST (relaxed hardware checks, --bypass-hardware-check)"
Write-Host ""

# ============================================================
# Summary
# ============================================================
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  Both packages assembled successfully!" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host ""

foreach ($dir in @($ProdDir, $VmDir)) {
    $name = Split-Path $dir -Leaf
    $totalSize = (Get-ChildItem $dir -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host "  $name/" -ForegroundColor White
    Get-ChildItem $dir -Force | ForEach-Object {
        $icon = if ($_.PSIsContainer) { "[DIR]" } else { "     " }
        $size = if ($_.PSIsContainer) { "" } else { "({0:N0} KB)" -f ($_.Length / 1KB) }
        Write-Host "    $icon $($_.Name) $size"
    }
    Write-Host ("    Total: {0:N1} MB" -f ($totalSize / 1MB))
    Write-Host ""
}

Write-Host "  Output: $OutputRoot" -ForegroundColor White
Write-Host ""
Write-Host "  Deployment: Copy the desired package folder to a USB drive" -ForegroundColor DarkGray
Write-Host "              or network share, add SW\ installers, then" -ForegroundColor DarkGray
Write-Host "              run INSTALL.bat as Administrator." -ForegroundColor DarkGray
Write-Host ""
