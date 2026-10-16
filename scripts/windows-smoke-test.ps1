# Starts the app for a few seconds to prove it actually launches: catches
# missing dependencies, XAML/resource errors and startup crashes before
# anything is published.
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

Stop-Process -Id $process.Id -Force
Write-Host "smoke test passed: $Exe stayed up for $Seconds s"
exit 0
