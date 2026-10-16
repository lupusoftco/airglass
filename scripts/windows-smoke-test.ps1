# Starts the app for a few seconds to prove it actually launches: catches
# missing dependencies, XAML/resource errors and startup crashes before
# anything is published. Then checks that its local server serves the viewer
# page and turns away a viewer without a valid token.
#
# usage: scripts/windows-smoke-test.ps1 -Exe dist/AirGlass-x.y.z.exe [-Seconds 8]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$Seconds = 8
)
$ErrorActionPreference = "Stop"

$process = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
Start-Sleep -Seconds $Seconds

if ($process.HasExited) {
    Write-Error "smoke test FAILED: $Exe exited within $Seconds s (exit code $($process.ExitCode))"
    exit 1
}

function Fail([string]$message) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $log = Join-Path $env:LOCALAPPDATA "AirGlass\AirGlass.log"
    if (Test-Path $log) { Get-Content $log | Write-Host }
    Write-Error "smoke test FAILED: $message"
    exit 1
}

# The server takes 3131, or the next free port up to 3140.
$base = $null
foreach ($port in 3131..3140) {
    try {
        $page = Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 3 -SkipHttpErrorCheck
        if ($page.StatusCode -eq 200 -and $page.Content -match "AirGlass") { $base = "http://127.0.0.1:$port"; break }
    } catch { }
}
if (-not $base) { Fail "the viewer page is not served on ports 3131-3140" }

$range = Invoke-WebRequest -Uri "$base/keepawake.mp4" -Headers @{ Range = "bytes=0-1" } -UseBasicParsing -SkipHttpErrorCheck
if ($range.StatusCode -ne 206) { Fail "range request returned $($range.StatusCode), expected 206" }

$hello = Invoke-WebRequest -Uri "$base/signal/hello" -Method Post -Body '{"token":"wrong"}' `
    -ContentType "application/json" -UseBasicParsing -SkipHttpErrorCheck
if ($hello.StatusCode -ne 403) { Fail "hello with a wrong token returned $($hello.StatusCode), expected 403" }

$poll = Invoke-WebRequest -Uri "$base/signal/poll" -UseBasicParsing -SkipHttpErrorCheck
if ($poll.StatusCode -ne 403) { Fail "poll without a session returned $($poll.StatusCode), expected 403" }

$missing = Invoke-WebRequest -Uri "$base/nope" -UseBasicParsing -SkipHttpErrorCheck
if ($missing.StatusCode -ne 404) { Fail "unknown path returned $($missing.StatusCode), expected 404" }

Stop-Process -Id $process.Id -Force
Write-Host "smoke test passed: $Exe stayed up for $Seconds s and serves the viewer on $base"
exit 0
