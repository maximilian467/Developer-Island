# Builds and starts Developer Island from source (no installer, no need to find the exe).
#   .\run.cmd              normal mode, your real data
#   .\run.cmd -Demo        demo data, separate demo settings
#   .\run.cmd -Release     optimized build (slower to build, what users get)
param(
    [switch]$Demo,
    [switch]$Release
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
. "$PSScriptRoot\dotnet-sdk.ps1"
$dotnet = Use-DotnetSdk

$configuration = if ($Release) { "Release" } else { "Debug" }
$appArgs = @()
if ($Demo) { $appArgs += "--demo" }

Write-Host "Building and starting Developer Island ($configuration$(if ($Demo) { ', demo' }))..." -ForegroundColor Cyan
Write-Host "The island appears at the top of your screen. Quit it from the tray icon (right-click, Quit)."
& $dotnet run --project "$root\src\DeveloperIsland" -c $configuration -- @appArgs
if ($LASTEXITCODE -ne 0) { throw "Developer Island exited with code $LASTEXITCODE" }
