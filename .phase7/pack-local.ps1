param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0-preview.1",
    [string]$OutputPath = "artifacts\nuget"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root $OutputPath

if (-not ([System.IO.Path]::GetFullPath($out).StartsWith([System.IO.Path]::GetFullPath($root), [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to write outside workspace: $out"
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem $out -Filter "*.nupkg" -File -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $out -Filter "*.snupkg" -File -ErrorAction SilentlyContinue | Remove-Item -Force

function Invoke-CheckedDotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$projects = @(
    "src\Marionette.NET.Abstractions\Marionette.NET.Abstractions.csproj",
    "src\Marionette.NET.SourceGenerator\Marionette.NET.SourceGenerator.csproj",
    "src\Marionette.NET.Runtime\Marionette.NET.Runtime.csproj",
    "src\Marionette.NET.Adapter.Wpf\Marionette.NET.Adapter.Wpf.csproj",
    "src\Marionette.NET.Adapter.Avalonia\Marionette.NET.Adapter.Avalonia.csproj",
    "src\Marionette.NET.Adapter.WinUI\Marionette.NET.Adapter.WinUI.csproj",
    "src\Marionette.NET.Adapter.Maui\Marionette.NET.Adapter.Maui.csproj",
    "src\Marionette.NET.Testing\Marionette.NET.Testing.csproj",
    "src\Marionette.NET.Testing.Xunit\Marionette.NET.Testing.Xunit.csproj",
    "src\Marionette.NET.Testing.NUnit\Marionette.NET.Testing.NUnit.csproj",
    "src\Marionette.NET\Marionette.NET.csproj"
)

Write-Host "Building solution ($Configuration) before packing..."
Invoke-CheckedDotNet @("build", (Join-Path $root "Marionette.NET.sln"), "-c", $Configuration)

foreach ($project in $projects) {
    $full = Join-Path $root $project
    Write-Host "Packing $project..."
    Invoke-CheckedDotNet @(
        "pack",
        $full,
        "-c",
        $Configuration,
        "--no-build",
        "-p:PackageVersion=$Version",
        "-p:Version=$Version",
        "-o",
        $out)
}

Write-Host ""
Write-Host "Local packages written to $out"
Get-ChildItem $out -Filter "*.nupkg" | Sort-Object Name | ForEach-Object {
    Write-Host (" - " + $_.Name)
}
