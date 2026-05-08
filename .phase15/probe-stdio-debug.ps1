# Phase 15 probe with stderr capture for diagnosis.
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path 'samples/Sample.WinForms.OrderTracker/bin/Debug/net10.0-windows/Sample.WinForms.OrderTracker.exe').Path

function Read-ResponseFor {
    param([System.IO.StreamReader]$reader, [int]$id, [int]$timeoutMs = 5000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { return $null }
        if ($line -match '"id"\s*:\s*' + $id + '([,}\s])') { return $line }
    }
    return $null
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName  = $exePath
$psi.Arguments = '--mcp --headless'
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow  = $true

$proc = [System.Diagnostics.Process]::Start($psi)

# Drain stderr asynchronously into a buffer.
$stderrBuf = [System.Text.StringBuilder]::new()
$proc.add_ErrorDataReceived({ if ($null -ne $args[1].Data) { [void]$stderrBuf.AppendLine($args[1].Data) } })
$proc.BeginErrorReadLine()

try {
    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}')
    $proc.StandardInput.Flush()
    [void](Read-ResponseFor $proc.StandardOutput 1)

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $proc.StandardInput.Flush()

    # Try the dynamic per-method tool path (flat args, no `invoke_method` meta).
    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"OrderViewModel.AddOrder","arguments":{"customer":"Probe Inc","amount":42.5}}}')
    $proc.StandardInput.Flush()
    $line3 = Read-ResponseFor $proc.StandardOutput 3

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_observable","arguments":{"root":"OrderViewModel","name":"TotalOrders"}}}')
    $proc.StandardInput.Flush()
    $line4 = Read-ResponseFor $proc.StandardOutput 4

    Write-Host '=== AddOrder via dynamic tool ==='
    Write-Host $line3
    Write-Host '=== read_observable TotalOrders ==='
    Write-Host $line4
}
finally {
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(2000)) { try { $proc.Kill() } catch { } }
}

Write-Host '=== STDERR ==='
Write-Host $stderrBuf.ToString()
