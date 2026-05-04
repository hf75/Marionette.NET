param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0-preview.1",
    [switch]$SkipShowcasePublish
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "== Marionette.NET local Phase-7 release check =="
Write-Host "Workspace: $root"
Write-Host "Version:   $Version"
Write-Host ""

function Invoke-CheckedDotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Invoke-CheckedDotNet @("build", (Join-Path $root "Marionette.NET.sln"), "-c", "Debug")
Invoke-CheckedDotNet @("test", (Join-Path $root "tests\Marionette.NET.SourceGenerator.Tests\Marionette.NET.SourceGenerator.Tests.csproj"), "-c", "Debug", "--no-restore")
Invoke-CheckedDotNet @("test", (Join-Path $root "tests\Marionette.NET.Testing.Tests\Marionette.NET.Testing.Tests.csproj"), "-c", "Debug", "--no-restore")
Invoke-CheckedDotNet @("test", (Join-Path $root "tests\Marionette.NET.Integration\Marionette.NET.Integration.csproj"), "-c", "Debug", "--no-restore")

& (Join-Path $PSScriptRoot "pack-local.ps1") -Configuration $Configuration -Version $Version
& (Join-Path $PSScriptRoot "test-local-package-consumption.ps1") -Version $Version

if (-not $SkipShowcasePublish) {
    & (Join-Path $PSScriptRoot "publish-showcases.ps1") -Configuration $Configuration
    & (Join-Path $PSScriptRoot "dogfood-showcases.ps1")
}

& (Join-Path $PSScriptRoot "New-DemoGifs.ps1")

Write-Host ""
Write-Host "Local Phase-7 release check completed. No git push, NuGet push, or GitHub release was performed."
