<#
.SYNOPSIS
    Phase 1.4 - drives the StdioTest harness against Sample.Wpf.TodoApp.exe in
    the TodoApp-specific assertion mode (--todoapp).

.DESCRIPTION
    Wraps two artifacts:
      * StdioTest.dll  - the harness (extended for Phase 1.4 with --todoapp)
      * Sample.Wpf.TodoApp.exe - the Debug+MCP build

    The harness asserts:
      * MCP initialize handshake.
      * tools/list returns the four Marionette tools.
      * inspect_app_api returns the TodoListViewModel manifest with all five
        callables and four observables.
      * read_observable TotalCount initially returns 0.
      * invoke_method AddTodo("buy milk") succeeds.
      * read_observable TotalCount returns 1 after AddTodo.
      * resources/subscribe to marionette://TodoListViewModel/TotalCount
        followed by a second AddTodo produces a notifications/resources/updated.
      * capture_screenshot surfaces the documented 'screenshot_not_supported'
        error (NoOpAdapter - headless mode).

    Exit code 0 = PASS, non-zero = FAIL. The full harness output is forwarded
    to stdout for forensic value; the verdict line is printed by the harness
    itself.

.PARAMETER Configuration
    Build configuration (Debug|Release). Defaults to Debug; the typical Phase
    1.4 verification path uses the Debug+MCP_ENABLED build.

.EXAMPLE
    pwsh .phase1/test-todoapp.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot      = Split-Path -Parent $PSScriptRoot
$harnessDll    = Join-Path $repoRoot ".phase0\StdioTest\bin\$Configuration\net10.0\StdioTest.dll"
$todoAppExe    = Join-Path $repoRoot "samples\Sample.Wpf.TodoApp\bin\$Configuration\net10.0-windows\Sample.Wpf.TodoApp.exe"

if (-not (Test-Path $harnessDll)) {
    Write-Error "StdioTest harness not found at $harnessDll. Build it: dotnet build .phase0/StdioTest/StdioTest.csproj -c $Configuration"
    exit 2
}
if (-not (Test-Path $todoAppExe)) {
    Write-Error "Sample.Wpf.TodoApp.exe not found at $todoAppExe. Build it: dotnet build samples/Sample.Wpf.TodoApp/Sample.Wpf.TodoApp.csproj -c $Configuration -p:EnableMcpAutomation=true"
    exit 2
}

Write-Host "=== Phase 1.4 TodoApp test runner ==="
Write-Host "Harness: $harnessDll"
Write-Host "Sample:  $todoAppExe"
Write-Host ""

& dotnet $harnessDll $todoAppExe --todoapp
$harnessExit = $LASTEXITCODE

Write-Host ""
if ($harnessExit -eq 0) {
    Write-Host "PASS - TodoApp Phase-1.4 contract holds." -ForegroundColor Green
    exit 0
}
Write-Host "FAIL - TodoApp Phase-1.4 contract regression. Exit code: $harnessExit" -ForegroundColor Red
exit $harnessExit
