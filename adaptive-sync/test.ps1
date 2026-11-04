$ErrorActionPreference = 'Stop'

$localSdk = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localSdk
    $env:PATH = "$localSdk;$env:PATH"
}

Push-Location $PSScriptRoot
try {
    dotnet test AdaptiveSync.sln
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
