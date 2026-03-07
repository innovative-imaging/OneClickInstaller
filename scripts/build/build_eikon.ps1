$ErrorActionPreference = "Stop"
# Resolve repository root (this script is in scripts/build/)
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\.." )).Path

Write-Host "Building EikonConfigurator..."
dotnet build "$RepoRoot\src\EikonConfigurator\EikonConfigurator.csproj" -c Release

Write-Host "Building EikonConfigurator.Tests..."
dotnet build "$RepoRoot\tests\EikonConfigurator.Tests\EikonConfigurator.Tests.csproj" -c Release

Write-Host "Build Complete!"