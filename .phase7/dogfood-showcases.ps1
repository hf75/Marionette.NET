param(
    [string]$ShowcasePath = "artifacts\showcases"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$showcases = Join-Path $root $ShowcasePath
$harness = Join-Path $root ".phase0\StdioTest\bin\Debug\net10.0\StdioTest.dll"

function Invoke-CheckedDotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Invoke-CheckedDotNet @("build", (Join-Path $root ".phase0\StdioTest\StdioTest.csproj"), "-c", "Debug")

$cases = @(
    @{ Name = "wpf-todo"; Exe = "wpf-todo\Sample.Wpf.TodoApp.exe"; Mode = "--todoapp" },
    @{ Name = "avalonia-dashboard"; Exe = "avalonia-dashboard\Sample.Avalonia.Dashboard.exe"; Mode = "--avalonia" },
    @{ Name = "winui-formlab"; Exe = "winui-formlab\Sample.WinUI.FormLab.exe"; Mode = "--winui" }
)

foreach ($case in $cases) {
    $exe = Join-Path $showcases $case.Exe
    if (-not (Test-Path $exe)) {
        throw "Showcase executable not found: $exe"
    }

    Write-Host "Dogfooding $($case.Name)..."
    Invoke-CheckedDotNet @("exec", $harness, (Resolve-Path $exe), $case.Mode)
}

Write-Host "Showcase dogfood smoke tests passed."
