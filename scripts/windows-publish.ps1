# Builds the Windows app into one self-contained AirGlass-<version>.exe in
# dist/ (no .NET installation needed on the target machine) and smoke-tests
# it before it can be published.
#
# usage (PowerShell, from anywhere): scripts/windows-publish.ps1 [-Runtime win-x64]
param(
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$project = "Windows/AirGlass/AirGlass.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$output = "build/windows/$Runtime"

Write-Host "==> Publishing AirGlass $version ($Runtime)"
dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force -Path dist | Out-Null
$exe = "dist/AirGlass-$version.exe"
Copy-Item "$output/AirGlass.exe" $exe -Force

Write-Host "==> Smoke-testing $exe"
& "$PSScriptRoot/windows-smoke-test.ps1" -Exe $exe
if ($LASTEXITCODE -ne 0) {
    Remove-Item $exe
    throw "Smoke test failed"
}

Write-Host $exe
