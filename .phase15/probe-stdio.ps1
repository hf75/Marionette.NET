# Phase 15 stdio probe — initialize + tools/list + invoke + read_observable.
# Sends 4 JSON-RPC requests to a Sample.WinForms.OrderTracker --mcp --headless
# child process and verifies the responses. Skips any `notifications/*`
# unsolicited frames the server sends (tools/list_changed during dynamic
# tool registration is normal startup chatter).

$ErrorActionPreference = 'Stop'

$exePath = if ($args.Count -ge 1) { $args[0] } else {
    'samples/Sample.WinForms.OrderTracker/bin/Debug/net10.0-windows/Sample.WinForms.OrderTracker.exe'
}

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found: $exePath"
    exit 2
}

function Read-ResponseFor {
    param([System.IO.StreamReader]$reader, [int]$id, [int]$timeoutMs = 5000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { return $null }
        # Notification frames have no "id" field — skip them.
        if ($line -match '"id"\s*:\s*' + $id + '([,}\s])') { return $line }
        if ($line -match '"id"\s*:\s*"' + $id + '"') { return $line }
        # else loop and read next.
    }
    return $null
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName  = (Resolve-Path $exePath).Path
$psi.Arguments = '--mcp --headless'
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow  = $true

$proc = [System.Diagnostics.Process]::Start($psi)

try {
    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"phase15-probe","version":"1"}}}')
    $proc.StandardInput.Flush()
    $line1 = Read-ResponseFor $proc.StandardOutput 1

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $proc.StandardInput.Flush()

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $proc.StandardInput.Flush()
    $line2 = Read-ResponseFor $proc.StandardOutput 2

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"OrderViewModel.AddOrder","arguments":{"customer":"Probe Inc","amount":42.5}}}')
    $proc.StandardInput.Flush()
    $line3 = Read-ResponseFor $proc.StandardOutput 3

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_observable","arguments":{"root":"OrderViewModel","property":"TotalOrders"}}}')
    $proc.StandardInput.Flush()
    $line4 = Read-ResponseFor $proc.StandardOutput 4

    Write-Host "=== initialize response ==="
    if ($line1) { Write-Host $line1 } else { Write-Host '(no response)' }
    Write-Host "=== tools/list response (first 800 chars) ==="
    if ($line2) {
        if ($line2.Length -gt 800) { Write-Host ($line2.Substring(0, 800) + ' ...[truncated]') } else { Write-Host $line2 }
    } else { Write-Host '(no response)' }
    Write-Host "=== invoke AddOrder response ==="
    if ($line3) { Write-Host $line3 } else { Write-Host '(no response)' }
    Write-Host "=== read_observable TotalOrders response ==="
    if ($line4) { Write-Host $line4 } else { Write-Host '(no response)' }
}
finally {
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(2000)) {
        try { $proc.Kill() } catch { }
    }
}

$pass = $true
foreach ($line in @($line1, $line2, $line3, $line4)) {
    if (-not $line -or -not ($line.StartsWith('{'))) { $pass = $false }
}
if ($line1 -notmatch '"protocolVersion"') { $pass = $false; Write-Host '  -- initialize missing protocolVersion' }
if ($line2 -notmatch 'OrderViewModel\.AddOrder') { $pass = $false; Write-Host '  -- tools/list missing OrderViewModel.AddOrder' }
if ($line3 -notmatch '"result"' -or $line3 -match '"isError"\s*:\s*true') { $pass = $false; Write-Host '  -- invoke response missing result or isError=true' }
if ($line4 -notmatch '"result"' -or $line4 -match '"isError"\s*:\s*true') { $pass = $false; Write-Host '  -- read_observable response missing result or isError=true' }

if ($pass) {
    Write-Host "`n[probe] VERDICT: PASS (4/4 frames)"
    exit 0
} else {
    Write-Host "`n[probe] VERDICT: FAIL"
    exit 1
}
