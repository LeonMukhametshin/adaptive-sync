param(
    [Parameter(Mandatory = $true)][string]$LogFile,
    [string]$ExecuteMethod,
    [string]$BuildTarget,
    [string]$TestPlatform,
    [string]$TestResults,
    [string]$TestFilter,
    [string]$UnityVersion = '6000.0.64f1'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$unity = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
if (-not (Test-Path $unity)) {
    throw "Unity $UnityVersion is not installed at $unity"
}

$projectPath = $repositoryRoot
if ($repositoryRoot -match '[^\x00-\x7F]') {
    $projectPath = Join-Path $env:USERPROFILE 'dms-unity'
    if (-not (Test-Path $projectPath)) {
        New-Item -ItemType Junction -Path $projectPath -Target $repositoryRoot | Out-Null
    }
    if ((Get-Item $projectPath).Target -ne $repositoryRoot) {
        throw "$projectPath exists but does not point to $repositoryRoot"
    }
}

$arguments = @('-batchmode', '-projectPath', "`"$projectPath`"", '-logFile', "`"$LogFile`"")
if ($BuildTarget) { $arguments += @('-buildTarget', $BuildTarget) }
if ($ExecuteMethod) { $arguments += @('-executeMethod', $ExecuteMethod, '-quit') }
if ($TestPlatform) {
    $arguments += @('-runTests', '-testPlatform', $TestPlatform)
    if ($TestResults) { $arguments += @('-testResults', "`"$TestResults`"") }
    if ($TestFilter) { $arguments += @('-testFilter', $TestFilter) }
}

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $unity
$info.Arguments = $arguments -join ' '
$info.UseShellExecute = $false
$info.Environment.Clear()

foreach ($scope in 'Machine', 'User') {
    $variables = [Environment]::GetEnvironmentVariables($scope)
    foreach ($name in $variables.Keys) {
        $value = [string]$variables[$name]
        if ($name -ieq 'Path' -or $value.Length -gt 1024) { continue }
        $info.Environment[$name] = $value
    }
}
$info.Environment['Path'] = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$processVariables = 'USERPROFILE', 'USERNAME', 'USERDOMAIN', 'COMPUTERNAME', 'APPDATA', 'LOCALAPPDATA', 'HOMEDRIVE', 'HOMEPATH',
    'SystemDrive', 'SystemRoot', 'windir', 'ProgramData', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432',
    'CommonProgramFiles', 'CommonProgramFiles(x86)', 'CommonProgramW6432', 'ALLUSERSPROFILE', 'PUBLIC',
    'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER', 'PROCESSOR_LEVEL', 'PROCESSOR_REVISION', 'NUMBER_OF_PROCESSORS', 'OS'
foreach ($name in $processVariables) {
    $value = [Environment]::GetEnvironmentVariable($name, 'Process')
    if ($value) { $info.Environment[$name] = $value }
}

$process = [System.Diagnostics.Process]::Start($info)
$process.WaitForExit()
Write-Host "Unity exited with code $($process.ExitCode)"
exit $process.ExitCode
