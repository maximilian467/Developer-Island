# Shared by the tools scripts (dot-source it): finds a dotnet that has a .NET 10 SDK and makes the
# current process use it. The dotnet on PATH can be an older machine-wide install with runtimes
# only, while the .NET 10 SDK is installed per user.
function Use-DotnetSdk([int]$Major = 10) {
    $candidates = @(
        $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        $((Get-Command dotnet.exe -All -ErrorAction SilentlyContinue).Source),
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks | Where-Object { $_ -match "^$Major\." }) {
            # Child processes (build tasks, test host, the app) must use the same installation.
            $env:DOTNET_ROOT = Split-Path $candidate
            $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
            $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            $env:DOTNET_NOLOGO = '1'
            return $candidate
        }
    }

    $searched = if ($candidates) { $candidates -join ', ' } else { 'none found' }
    throw ".NET $Major SDK not found (searched: $searched). Install it with: winget install Microsoft.DotNet.SDK.$Major"
}
