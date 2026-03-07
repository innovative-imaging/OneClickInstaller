$ErrorActionPreference = "Stop"
# Resolve repository root (this script is in scripts/build/)
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\.." )).Path
$PublishDir = Join-Path $RepoRoot "src\EikonConfigurator\bin\publish"

# Clean previous
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir | Out-Null

Write-Host "Publishing EikonConfigurator (Self-Contained, Single File)..."
dotnet publish "$RepoRoot\src\EikonConfigurator\EikonConfigurator.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -o "$PublishDir"

Write-Host "Copying NetFx3 Offline Installer..."
$NetFxSource = Join-Path $RepoRoot "All_SW\DirectX-SDK\Net Framework 3.5 offline installer 10"
$NetFxDest = Join-Path $PublishDir "NetFx3_Offline"
if (Test-Path $NetFxSource) {
    Copy-Item -Path $NetFxSource -Destination $NetFxDest -Recurse -Force
} else {
    Write-Warning "NetFx3 Offline Installer source not found at $NetFxSource"
}

Write-Host "`nBuild Successful!"
Write-Host "Published to: $PublishDir"
Write-Host "Run build_package.ps1 to assemble full deployment packages."