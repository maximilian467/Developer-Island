# Builds a Developer Island release: tests, self-contained publish, portable zip and installer.
#   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1 [-Version 1.0.0] [-Runtime win-x64] [-SkipTests] [-SkipInstaller]
# Requirements: .NET 10 SDK. Installer: Inno Setup 6 (ISCC.exe), e.g. `winget install JRSoftware.InnoSetup`.
param(
    [string]$Version,
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$SkipInstaller
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
if ($Runtime -ne 'win-x64' -and -not $SkipInstaller) {
    throw 'The V1 installer supports x64 only. Use -SkipInstaller for experimental ARM64 publishing.'
}
if (-not $Version) {
    $Version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must have three or four numeric components.' }
$platform = if ($Runtime -eq "win-arm64") { "ARM64" } else { "x64" }
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\$Runtime"
Write-Host "Developer Island $Version ($Runtime)" -ForegroundColor Cyan

# Find a dotnet that has a .NET 10 SDK. The one on PATH may only have runtimes (for example an
# older machine-wide install in Program Files), while the SDK lives in a per-user install.
$sdkMajor = 10
$candidates = @(
    $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    $((Get-Command dotnet.exe -All -ErrorAction SilentlyContinue).Source),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
$dotnet = $null
foreach ($candidate in $candidates) {
    $sdks = & $candidate --list-sdks 2>$null
    if ($sdks | Where-Object { $_ -match "^$sdkMajor\." }) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    $searched = if ($candidates) { $candidates -join ', ' } else { 'none found' }
    throw ".NET $sdkMajor SDK not found (searched: $searched). Install it with: winget install Microsoft.DotNet.SDK.$sdkMajor"
}
# Child processes (test host, build tasks) must use the same installation.
$env:DOTNET_ROOT = Split-Path $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Write-Host "Using $dotnet"

# A running copy of the published app locks its files.
$running = Get-Process DeveloperIsland -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith([IO.Path]::GetFullPath($publish), [StringComparison]::OrdinalIgnoreCase)
}
if ($running) { throw "Developer Island is running from $publish. Quit it (tray icon, Quit) and run the script again." }

if (-not $SkipTests) {
    & $dotnet test "$root\tests\DeveloperIsland.Tests" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

$publish = [IO.Path]::GetFullPath($publish)
$expectedPublishRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\publish')) + [IO.Path]::DirectorySeparatorChar
if (-not $publish.StartsWith($expectedPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish directory must stay inside artifacts/publish.'
}
if (Test-Path -LiteralPath $publish) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publish).Path
    if ($resolvedPublish -ne $publish -or (Get-Item -LiteralPath $publish).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to clean a redirected publish directory.'
    }
    Remove-Item -LiteralPath $publish -Recurse -Force
}
& $dotnet publish "$root\src\DeveloperIsland\DeveloperIsland.csproj" -c Release -r $Runtime -p:Platform=$platform `
    -p:Version=$Version -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Debug symbols are not needed by users.
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

$zip = Join-Path $artifacts "DeveloperIsland-$Version-$Runtime-portable.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path "$publish\*" -DestinationPath $zip
Write-Host "Portable: $zip"

if (-not $SkipInstaller) {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $iscc = @(
        $(if ($onPath) { $onPath.Source }),
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup" }
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$artifacts" "$root\installer\DeveloperIsland.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Installer: $(Join-Path $artifacts 'DeveloperIsland-Setup.exe')"
}
