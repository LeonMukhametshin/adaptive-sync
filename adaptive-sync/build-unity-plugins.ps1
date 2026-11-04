param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$localSdk = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localSdk
    $env:PATH = "$localSdk;$env:PATH"
}

$solutionRoot = $PSScriptRoot
$repositoryRoot = Split-Path $solutionRoot -Parent
$publishDir = Join-Path $solutionRoot 'artifacts\unity-plugins'
$pluginDir = Join-Path $repositoryRoot 'Assets\Plugins\AdaptiveSync'
$nativeDir = Join-Path $pluginDir 'x86_64'
$nativeLibrary = 'e_sqlite3.dll'

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish (Join-Path $solutionRoot 'src\Sync.Client\Sync.Client.csproj') `
    -c $Configuration -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

New-Item -ItemType Directory -Force $pluginDir | Out-Null
New-Item -ItemType Directory -Force $nativeDir | Out-Null

$managed = Get-ChildItem $publishDir -Filter *.dll | Where-Object { $_.Name -ne $nativeLibrary }
$managedNames = $managed | ForEach-Object { $_.Name }

Get-ChildItem $pluginDir -Filter *.dll | Where-Object { $managedNames -notcontains $_.Name } | ForEach-Object {
    Remove-Item $_.FullName -Force
    Remove-Item "$($_.FullName).meta" -Force -ErrorAction SilentlyContinue
}

function Copy-IfChanged([string]$Source, [string]$Destination) {
    if ((Test-Path $Destination) -and
        (Get-FileHash $Source).Hash -eq (Get-FileHash $Destination).Hash) {
        return
    }
    Copy-Item $Source $Destination -Force
}

foreach ($file in $managed) {
    Copy-IfChanged $file.FullName (Join-Path $pluginDir $file.Name)
}
Copy-IfChanged (Join-Path $publishDir $nativeLibrary) (Join-Path $nativeDir $nativeLibrary)

$deps = Get-Content (Join-Path $publishDir 'Sync.Client.deps.json') -Raw | ConvertFrom-Json
$sqliteVersion = ($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'SQLitePCLRaw.core/*' }) -replace '^.*/', ''
$androidPackage = 'SQLitePCLRaw.lib.e_sqlite3.android'
$restoreDir = Join-Path $solutionRoot 'artifacts\android-native'
New-Item -ItemType Directory -Force $restoreDir | Out-Null
Set-Content -Path (Join-Path $restoreDir 'android-native.csproj') -Encoding utf8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup>
  <ItemGroup><PackageDownload Include="$androidPackage" Version="[$sqliteVersion]" /></ItemGroup>
</Project>
"@
dotnet restore (Join-Path $restoreDir 'android-native.csproj')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$aar = Get-ChildItem (Join-Path $packagesRoot "$($androidPackage.ToLowerInvariant())\$sqliteVersion") -Recurse -Filter *.aar | Select-Object -First 1
$androidDir = Join-Path $pluginDir 'Android\arm64-v8a'
New-Item -ItemType Directory -Force $androidDir | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($aar.FullName)
try {
    $entry = $archive.GetEntry('jni/arm64-v8a/libe_sqlite3.so')
    $extracted = Join-Path $restoreDir 'libe_sqlite3.so'
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extracted, $true)
    Copy-IfChanged $extracted (Join-Path $androidDir 'libe_sqlite3.so')
}
finally {
    $archive.Dispose()
}

Write-Host "Copied $($managed.Count) managed assemblies, $nativeLibrary and Android arm64 libe_sqlite3.so (SQLitePCLRaw $sqliteVersion) to $pluginDir"
