param(
    [string]$Version = "0.1.0-preview.1",
    [string]$PackageSource = "artifacts\nuget",
    [string]$ScratchPath = "artifacts\consume-wpf"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$source = Join-Path $root $PackageSource
$scratch = Join-Path $root $ScratchPath

if (-not (Test-Path $source)) {
    throw "Package source does not exist: $source"
}

if (-not ([System.IO.Path]::GetFullPath($scratch).StartsWith([System.IO.Path]::GetFullPath($root), [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to create scratch project outside workspace: $scratch"
}

function Invoke-CheckedDotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

foreach ($id in @("marionette.net", "marionette.net.abstractions", "marionette.net.runtime")) {
    $cached = Join-Path $env:USERPROFILE ".nuget\packages\$id\$Version"
    if (Test-Path $cached) {
        Remove-Item -Recurse -Force -LiteralPath $cached
    }
}

if (Test-Path $scratch) {
    Remove-Item -Recurse -Force -LiteralPath $scratch
}

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
Push-Location $scratch
try {
    Invoke-CheckedDotNet @("new", "wpf", "-n", "ConsumeWpf", "-f", "net10.0")
    Push-Location ".\ConsumeWpf"
    try {
        (Get-Content ".\ConsumeWpf.csproj") `
            -replace "<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net10.0-windows</TargetFramework>" |
            Set-Content ".\ConsumeWpf.csproj" -Encoding UTF8

        Invoke-CheckedDotNet @("add", "package", "Marionette.NET", "--version", $Version, "--source", $source)

        @'
using Marionette;

namespace ConsumeWpf;

[McpRoot]
public sealed class DemoRoot
{
    [McpCallable("Adds two numbers.")]
    public int Add(int a, int b) => a + b;
}
'@ | Set-Content -Path ".\DemoRoot.cs" -Encoding UTF8

        Invoke-CheckedDotNet @("build", "-c", "Debug")
    }
    finally {
        Pop-Location
    }
}
finally {
    Pop-Location
}

Write-Host "Local package consumption smoke test passed."
