param(
    [string]$OutputPath = "docs\media"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root $OutputPath

if (-not ([System.IO.Path]::GetFullPath($out).StartsWith([System.IO.Path]::GetFullPath($root), [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to write outside workspace: $out"
}

$ffmpeg = Get-Command ffmpeg -ErrorAction Stop
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-MarionetteGif {
    param(
        [string]$FileName,
        [string]$Title,
        [string]$Step1,
        [string]$Step2,
        [string]$Step3,
        [string]$Accent
    )

    $target = Join-Path $out $FileName
    $filter = @(
        "drawtext=text='$Title':fontcolor=white:fontsize=38:x=78:y=84",
        "drawtext=text='inspect_app_api':fontcolor=0x8bd3ff:fontsize=28:x='if(lt(t,0.6),-600,78)':y=170",
        "drawtext=text='$Step1':fontcolor=0xd7e3f4:fontsize=24:x=112:y=218:enable='gte(t,0.8)'",
        "drawtext=text='invoke_method':fontcolor=0x9ef0b7:fontsize=28:x='if(lt(t,1.7),-600,78)':y=278",
        "drawtext=text='$Step2':fontcolor=0xd7e3f4:fontsize=24:x=112:y=326:enable='gte(t,1.9)'",
        "drawtext=text='read_observable + screenshot':fontcolor=0xffd166:fontsize=28:x='if(lt(t,2.9),-900,78)':y=386",
        "drawtext=text='$Step3':fontcolor=0xd7e3f4:fontsize=24:x=112:y=434:enable='gte(t,3.1)'",
        "drawtext=text='Marionette.NET preview':fontcolor=${Accent}:fontsize=20:x='78+mod(t*160,560)':y=486"
    ) -join ","

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & $ffmpeg.Source -y `
        -loglevel error `
        -f lavfi `
        -i "color=c=0x0b1220:s=960x540:d=4.4:r=12" `
        -vf $filter `
        -loop 0 `
        $target 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    Start-Sleep -Milliseconds 200
    $created = Get-Item $target -ErrorAction SilentlyContinue
    if ($exitCode -ne 0 -and (-not $created -or $created.Length -le 0)) {
        throw "ffmpeg failed to create $target with exit code $exitCode"
    }
}

New-MarionetteGif `
    -FileName "wpf-todo.gif" `
    -Title "WPF TodoApp dogfood" `
    -Step1 "TodoListViewModel exposes 5 callables and 4 observables" `
    -Step2 "AddTodo with Release checklist" `
    -Step3 "TotalCount changed and LastAddedTitle verified" `
    -Accent "0x56ccf2"

New-MarionetteGif `
    -FileName "avalonia-dashboard.gif" `
    -Title "Avalonia Dashboard dogfood" `
    -Step1 "DashboardViewModel exposes metrics and events" `
    -Step2 "UpsertMetric Latency with value 42" `
    -Step3 "Total and MetricUpserted event verified" `
    -Accent "0x9bff9c"

New-MarionetteGif `
    -FileName "winui-formlab.gif" `
    -Title "WinUI FormLab dogfood" `
    -Step1 "FormLabViewModel exposes form state" `
    -Step2 "SetName then SetAge then ToggleNotifications then Submit" `
    -Step3 "SubmittedCount and snapshot state verified" `
    -Accent "0xff8c42"

Write-Host "Demo GIFs written to $out"
