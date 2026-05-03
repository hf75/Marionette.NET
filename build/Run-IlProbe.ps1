<#
.SYNOPSIS
    Runs the Phase-0 IL symbol probe over a build output directory and asserts
    that no forbidden symbols leak into a stripped Release build.

.DESCRIPTION
    Wraps `.phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll`, invoking it once per
    forbidden needle. ProbeIl already exits 0 when total hits == 0 and 1 otherwise;
    this script aggregates exit codes across all needles and produces a single
    pass/fail summary that GitHub Actions can act on.

    Output format from ProbeIl (per Spike A):
        === <file> (assembly=<name>) ===
          ASSEMBLY-REF / TYPE-REF / TYPE-DEF / MEMBER-REF lines
          -> <file>: N hit(s)
        TOTAL hits across N file(s): X

    The script tees ProbeIl's full output to a log so CI artifacts include
    forensic detail when a regression triggers a failure.

.PARAMETER ProbeDll
    Absolute path to the compiled ProbeIl.dll.

.PARAMETER Target
    File or directory to inspect. The most useful regression check passes the
    sample's primary managed DLL (e.g. samples/.../bin/Release/.../Sample.Wpf.StripeProbe.dll)
    so that the Abstractions DLL, which legitimately contains the Marionette.Ai
    TypeDef, is excluded from the search. Pass a directory instead if you want
    a wider sweep — every .dll and .exe in the directory is scanned.

.PARAMETER Needles
    Array of forbidden symbol substrings. The default mirrors the four needles
    locked in Spike A's pass criteria.

.PARAMETER LogPath
    Optional path for the captured ProbeIl output log (default: ./ilprobe.log).

.EXAMPLE
    pwsh build/Run-IlProbe.ps1 `
        -ProbeDll .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll `
        -Target   samples/Sample.Wpf.StripeProbe/bin/Release/net10.0-windows/Sample.Wpf.StripeProbe.dll

.NOTES
    Exit codes:
        0  All needles produced 0 hits  (PASS — stripping promise holds)
        1  At least one needle produced >= 1 hit (FAIL — regression)
        2  ProbeIl could not be invoked at all (configuration error)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProbeDll,

    [Parameter(Mandatory)]
    [Alias('TargetDir', 'TargetFile')]
    [string]$Target,

    [string[]]$Needles = @(
        'Marionette.NET.Runtime',
        'Adapter.Wpf',
        'Adapter.Avalonia',
        'Marionette.Ai',
        'ModelContextProtocol'
    ),

    [string]$LogPath = (Join-Path (Get-Location) 'ilprobe.log')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ProbeDll)) {
    Write-Error "ProbeIl.dll not found at: $ProbeDll"
    exit 2
}
if (-not (Test-Path $Target)) {
    Write-Error "Target file or directory not found: $Target"
    exit 2
}

# Reset log file.
if (Test-Path $LogPath) { Remove-Item -Path $LogPath -Force }

$failures = @()
$summary = [ordered]@{}

Write-Host "=== Marionette.NET IL stripping regression check ==="
Write-Host "Probe:  $ProbeDll"
Write-Host "Target: $Target"
Write-Host "Log:    $LogPath"
Write-Host "Needles: $($Needles -join ', ')"
Write-Host ""

foreach ($needle in $Needles) {
    Write-Host "--- Needle: $needle ---"
    $banner = "===== Needle: $needle ====="
    Add-Content -Path $LogPath -Value $banner

    # Capture both stdout and stderr; tee to log; preserve ProbeIl's exit code.
    $output = & dotnet $ProbeDll $needle $Target 2>&1
    $probeExit = $LASTEXITCODE

    Add-Content -Path $LogPath -Value $output
    Add-Content -Path $LogPath -Value ""

    # Surface the TOTAL line specifically — it's the human-readable verdict.
    $totalLine = $output | Where-Object { $_ -match '^TOTAL hits across' } | Select-Object -Last 1
    if ($null -eq $totalLine) {
        Write-Warning "ProbeIl produced no TOTAL line for '$needle' (exit=$probeExit). Output:"
        $output | ForEach-Object { Write-Warning "  $_" }
        $failures += $needle
        $summary[$needle] = "MALFORMED (exit=$probeExit)"
        continue
    }

    Write-Host "  $totalLine"
    $summary[$needle] = $totalLine

    if ($probeExit -ne 0) {
        $failures += $needle
    }
}

Write-Host ""
Write-Host "=== Summary ==="
foreach ($k in $summary.Keys) {
    $status = if ($failures -contains $k) { 'FAIL' } else { 'PASS' }
    Write-Host ("  [{0}] {1}: {2}" -f $status, $k, $summary[$k])
}
Write-Host ""

if ($failures.Count -eq 0) {
    Write-Host "PASS — stripped build contains zero forbidden symbols."
    exit 0
}

Write-Host "FAIL — $($failures.Count) needle(s) leaked into stripped build: $($failures -join ', ')" -ForegroundColor Red
Write-Host "Inspect $LogPath for the full IL probe output."
exit 1
