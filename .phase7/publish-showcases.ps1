param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputPath = "artifacts\showcases"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$outRoot = Join-Path $root $OutputPath

if (-not ([System.IO.Path]::GetFullPath($outRoot).StartsWith([System.IO.Path]::GetFullPath($root), [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to publish outside workspace: $outRoot"
}

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

function Invoke-CheckedDotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$showcases = @(
    @{ Name = "wpf-todo"; Project = "samples\Sample.Wpf.TodoApp\Sample.Wpf.TodoApp.csproj" },
    @{ Name = "avalonia-dashboard"; Project = "samples\Sample.Avalonia.Dashboard\Sample.Avalonia.Dashboard.csproj" },
    @{ Name = "winui-formlab"; Project = "samples\Sample.WinUI.FormLab\Sample.WinUI.FormLab.csproj" }
)

foreach ($showcase in $showcases) {
    $project = Join-Path $root $showcase.Project
    $out = Join-Path $outRoot $showcase.Name
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    Write-Host "Publishing $($showcase.Name)..."
    Invoke-CheckedDotNet @(
        "publish",
        $project,
        "-c",
        $Configuration,
        "-r",
        $RuntimeIdentifier,
        "--self-contained",
        "false",
        "-p:EnableMcpAutomation=true",
        "-p:PublishSingleFile=false",
        "-o",
        $out)
}

Write-Host ""
Write-Host "Showcase apps written to $outRoot"
Get-ChildItem $outRoot -Directory | Sort-Object Name | ForEach-Object {
    $exe = Get-ChildItem $_.FullName -Filter "*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($exe) {
        Write-Host (" - " + $_.Name + ": " + $exe.FullName)
    } else {
        Write-Host (" - " + $_.Name + ": no .exe found")
    }
}
