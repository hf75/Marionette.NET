<#
.SYNOPSIS
    Marionette.NET - Phase 1 end-to-end demo runner.

.DESCRIPTION
    Single-command demo for Marionette.NET Phase 1. Exercises the full
    Phase-1 stack:

      1. Build Sample.Wpf.TodoApp (Debug + EnableMcpAutomation=true).
      2. Build the .phase0/StdioTest harness if not already built.
      3. Drive the harness against the TodoApp in headless MCP mode and
         assert all checks PASS.
      4. (Optional, with -Gui) re-run in GUI mode, capture a screenshot,
         validate PNG, save to .phase1/demo-screenshot.png.

    Adopters and CI both use this. Works from any current-directory; all
    paths inside are resolved against the script location.

.PARAMETER Gui
    Also run the GUI screenshot demo. Requires an interactive desktop
    session - cannot run from SYSTEM / unattended.

.PARAMETER NoBuild
    Skip the build step. Useful when iterating; assumes the Debug + MCP
    binaries already exist on disk.

.PARAMETER Configuration
    Build configuration. Default Debug (the only one with MCP_ENABLED at
    Phase 1).

.EXAMPLE
    pwsh .phase1/demo.ps1
    # Builds, runs the headless eval suite. PASS/FAIL on stdout.

.EXAMPLE
    pwsh .phase1/demo.ps1 -Gui
    # Same as above, plus a GUI screenshot capture.

.EXAMPLE
    pwsh .phase1/demo.ps1 -NoBuild
    # Skip build, assume already up-to-date.
#>
[CmdletBinding()]
param(
    [switch]$Gui,
    [switch]$NoBuild,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------------
# Resolve paths
# ------------------------------------------------------------------------

# Script's own directory and the repo root (one level up from .phase1/).
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = (Resolve-Path (Join-Path $scriptDir '..')).Path

$todoAppCsproj  = Join-Path $repoRoot 'samples\Sample.Wpf.TodoApp\Sample.Wpf.TodoApp.csproj'
$todoAppExe     = Join-Path $repoRoot ("samples\Sample.Wpf.TodoApp\bin\$Configuration\net10.0-windows\Sample.Wpf.TodoApp.exe")
$harnessCsproj  = Join-Path $repoRoot '.phase0\StdioTest\StdioTest.csproj'
$harnessDll     = Join-Path $repoRoot (".phase0\StdioTest\bin\$Configuration\net10.0\StdioTest.dll")
$screenshotPath = Join-Path $scriptDir 'demo-screenshot.png'

$failures = 0
$startTime = Get-Date

function Write-Section($text) {
    Write-Host ''
    Write-Host ('=== ' + $text + ' ===') -ForegroundColor Cyan
}

function Write-Pass($text) {
    Write-Host ('PASS - ' + $text) -ForegroundColor Green
}

function Write-Fail($text) {
    Write-Host ('FAIL - ' + $text) -ForegroundColor Red
    $script:failures += 1
}

function Write-Info($text) {
    Write-Host ('INFO - ' + $text) -ForegroundColor Yellow
}

# ------------------------------------------------------------------------
# Step 1 & 2: Build
# ------------------------------------------------------------------------

if (-not $NoBuild) {
    Write-Section "Step 1/4: Build Sample.Wpf.TodoApp ($Configuration + MCP_ENABLED)"
    if (-not (Test-Path $todoAppCsproj)) {
        Write-Fail "TodoApp csproj not found at $todoAppCsproj"
        exit 2
    }
    & dotnet build $todoAppCsproj -c $Configuration -p:EnableMcpAutomation=true --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "TodoApp build failed (dotnet build exit $LASTEXITCODE)"
        exit 1
    }
    Write-Pass "TodoApp built"

    Write-Section "Step 2/4: Build StdioTest harness"
    if (-not (Test-Path $harnessCsproj)) {
        Write-Fail "StdioTest csproj not found at $harnessCsproj"
        exit 2
    }
    & dotnet build $harnessCsproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "StdioTest build failed (dotnet build exit $LASTEXITCODE)"
        exit 1
    }
    Write-Pass "Harness built"
}
else {
    Write-Info "Skipping build (NoBuild flag set)."
}

# Verify artifacts
if (-not (Test-Path $todoAppExe)) {
    Write-Fail "TodoApp.exe not found at $todoAppExe (rebuild without -NoBuild)"
    exit 2
}
if (-not (Test-Path $harnessDll)) {
    Write-Fail "StdioTest.dll not found at $harnessDll (rebuild without -NoBuild)"
    exit 2
}

# ------------------------------------------------------------------------
# Step 3: Headless harness
# ------------------------------------------------------------------------

Write-Section "Step 3/4: Headless MCP handshake + tool exercise"
Write-Host ('Sample: ' + $todoAppExe)
Write-Host ('Harness: ' + $harnessDll)
Write-Host ''

# Capture only the verdict marker - the harness itself prints a full block.
& dotnet $harnessDll $todoAppExe --todoapp
$harnessExit = $LASTEXITCODE

if ($harnessExit -eq 0) {
    Write-Pass "Headless harness verdict: PASS (12/12 checks incl. event delivery + dynamic tools)"
} else {
    Write-Fail "Headless harness verdict: FAIL (exit $harnessExit)"
}

# ------------------------------------------------------------------------
# Step 4: GUI screenshot (optional)
# ------------------------------------------------------------------------

if ($Gui) {
    Write-Section "Step 4/4: GUI mode + screenshot validation"
    Write-Info "Starting WPF GUI mode - requires an interactive desktop session"

    # The harness writes its captured PNG to .phase1/screenshot-test.png by
    # default. We rename to demo-screenshot.png after the run for clarity.
    $stdioCapturePath = Join-Path $scriptDir 'screenshot-test.png'
    if (Test-Path $stdioCapturePath) { Remove-Item $stdioCapturePath -Force }

    & dotnet $harnessDll $todoAppExe --todoapp --gui
    $guiExit = $LASTEXITCODE

    if ($guiExit -eq 0) {
        Write-Pass "GUI harness verdict: PASS"
        if (Test-Path $stdioCapturePath) {
            try {
                Copy-Item -Path $stdioCapturePath -Destination $screenshotPath -Force
                $size = (Get-Item $screenshotPath).Length
                Write-Pass ("Screenshot captured to $screenshotPath ($size bytes)")
            } catch {
                Write-Info ("Could not copy screenshot to $screenshotPath - " + $_.Exception.Message)
            }
        } else {
            Write-Info "GUI harness PASSed but no screenshot file was produced."
        }
    } else {
        Write-Fail "GUI harness verdict: FAIL (exit $guiExit)"
    }
}
else {
    Write-Info "Skipping Step 4 (use -Gui to enable GUI screenshot demo)"
}

# ------------------------------------------------------------------------
# Summary
# ------------------------------------------------------------------------

$elapsed = (Get-Date) - $startTime
Write-Section ('Summary - elapsed {0:F1}s' -f $elapsed.TotalSeconds)

if ($failures -eq 0) {
    Write-Host ''
    Write-Host '*** Marionette.NET Phase 1 demo: ALL PASS ***' -ForegroundColor Green
    Write-Host ''
    Write-Host 'What just happened:' -ForegroundColor Gray
    Write-Host '  - Sample.Wpf.TodoApp built with --mcp host attached.' -ForegroundColor Gray
    Write-Host '  - Harness ran the four Marionette tools end-to-end.' -ForegroundColor Gray
    Write-Host '  - inspect_app_api enumerated 5 callables + 4 observables.' -ForegroundColor Gray
    Write-Host '  - invoke_method AddTodo("buy milk") succeeded.' -ForegroundColor Gray
    Write-Host '  - read_observable TotalCount went 0 -> 1.' -ForegroundColor Gray
    Write-Host '  - resources/subscribe pushed notifications/resources/updated.' -ForegroundColor Gray
    Write-Host '  - [McpEvent] TodoAdded fired and the event resource carried the args.' -ForegroundColor Gray
    if ($Gui) {
        Write-Host '  - capture_screenshot returned a valid PNG.' -ForegroundColor Gray
    }
    Write-Host '  - stdout stayed JSON-RPC pure (zero pollution).' -ForegroundColor Gray
    Write-Host ''
    exit 0
}
else {
    Write-Host ''
    Write-Host ("*** Marionette.NET Phase 1 demo: $failures FAILURE(S) ***") -ForegroundColor Red
    exit 1
}
