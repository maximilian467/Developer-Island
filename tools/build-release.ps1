# Builds a Developer Island release: tests, self-contained publish, portable zip, installer,
# checksums and their signature.
#   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1 [-Version 0.1.0] [-Runtime win-x64] [-SkipTests] [-SkipInstaller] [-SkipSigning]
# Requirements: .NET 10 SDK. Installer: Inno Setup 6 (ISCC.exe), e.g. `winget install JRSoftware.InnoSetup`.
# Signing: DI_RELEASE_SIGNING_KEY names the private key file (outside the repository, see docs/RELEASING.md).
# The app installs an update only when SHA256SUMS-<version>.txt.sig verifies; -SkipSigning is for test builds.
param(
    [string]$Version,
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipSigning
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."

# Checked before the long build: a release without a signature cannot be installed by the app's update.
$signingKey = $env:DI_RELEASE_SIGNING_KEY
if (-not $SkipSigning) {
    if (-not $signingKey) { throw 'Set DI_RELEASE_SIGNING_KEY to the release signing key file (docs/RELEASING.md), or pass -SkipSigning for a test build.' }
    if (-not (Test-Path -LiteralPath $signingKey -PathType Leaf)) { throw "The signing key file in DI_RELEASE_SIGNING_KEY does not exist." }
    $signingKey = [IO.Path]::GetFullPath($signingKey)
    if ($signingKey.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The signing key must not live inside the repository.'
    }
    if (-not (Test-Path -LiteralPath "$root\src\DeveloperIsland.Core\Updates\release-signing-public-key.pem")) {
        throw 'The public key is missing from src/DeveloperIsland.Core/Updates; the app would refuse every update (docs/RELEASING.md).'
    }
}
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

. "$PSScriptRoot\dotnet-sdk.ps1"
$dotnet = Use-DotnetSdk
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

# The license and the third-party notices travel with every copy of the app.
Copy-Item (Join-Path $root "LICENSE") (Join-Path $publish "LICENSE.txt")
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") (Join-Path $publish "THIRD_PARTY_NOTICES.md")

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
    Write-Host "Installer: $(Join-Path $artifacts "DeveloperIsland-Setup-$Version.exe")"
}

# Checksums for the release page.
$files = @($zip) + @(Get-ChildItem $artifacts -Filter "DeveloperIsland-Setup-$Version.exe" | ForEach-Object FullName)
$sums = Join-Path $artifacts "SHA256SUMS-$Version.txt"
$files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) } | Set-Content -Encoding ascii $sums
Write-Host "Checksums: $sums"

# The setup is not code-signed, so the app trusts an update only through this signature of the checksums.
if ($SkipSigning) {
    Write-Warning 'Not signed (-SkipSigning): the app will refuse to install this release as an update.'
} else {
    & $dotnet run "$PSScriptRoot\release-signing.cs" -- sign $signingKey $sums
    if ($LASTEXITCODE -ne 0) { throw "Signing the checksums failed" }
    Write-Host "Signature: $sums.sig"
}
