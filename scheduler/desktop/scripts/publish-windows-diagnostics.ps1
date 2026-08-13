[CmdletBinding()]
param(
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$")]
    [string]$Version = "1.0.0-dev",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputPath,
    [string]$WorkerPath,
    [string]$AppPath
)

$ErrorActionPreference = "Stop"
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $desktopRoot "src\Scheduler.Diagnostics\Scheduler.Diagnostics.csproj"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $desktopRoot "artifacts\diagnostics-windows-x64"
}

$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot "artifacts"))
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$artifactPrefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputFullPath.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be inside the desktop artifacts directory: $artifactRoot"
}

if (-not (Test-Path $projectPath -PathType Leaf)) {
    throw "Diagnostics project was not found."
}

& dotnet restore $projectPath --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Diagnostics locked restore failed." }

Remove-Item -Recurse -Force $outputFullPath -ErrorAction SilentlyContinue
& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    "-p:Version=$Version" `
    --no-restore `
    --output $outputFullPath
if ($LASTEXITCODE -ne 0) { throw "Diagnostics publish failed." }

$diagnosticsPath = Join-Path $outputFullPath "Scheduler.Diagnostics.exe"
if (-not (Test-Path $diagnosticsPath -PathType Leaf)) {
    throw "Single-file diagnostics executable was not produced."
}

$unexpectedFiles = @(Get-ChildItem -LiteralPath $outputFullPath -File |
    Where-Object { $_.Name -ne "Scheduler.Diagnostics.exe" })
if ($unexpectedFiles.Count -gt 0) {
    throw "Diagnostics output contains files outside the single-file executable contract: $($unexpectedFiles.Name -join ', ')"
}

function Invoke-DiagnosticsSmoke {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][int]$ExpectedExitCode
    )

    $smokeOutput = & $diagnosticsPath @Arguments | Out-String
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        throw "Diagnostics smoke command failed: $($Arguments -join ' ')"
    }

    return $smokeOutput
}

Invoke-DiagnosticsSmoke -Arguments @("help") -ExpectedExitCode 0 | Out-Null
$versionOutput = Invoke-DiagnosticsSmoke -Arguments @("version") -ExpectedExitCode 0
if ($versionOutput.Trim() -ne "scheduler-diagnostics $Version") {
    throw "Diagnostics version smoke output did not match the requested release version: $versionOutput"
}
Invoke-DiagnosticsSmoke -Arguments @("self-test") -ExpectedExitCode 0 | Out-Null
$doctorJson = Invoke-DiagnosticsSmoke -Arguments @("doctor", "--format", "json") -ExpectedExitCode 0
$doctor = $doctorJson | ConvertFrom-Json
if ($doctor.schema_version -ne 1 -or $doctor.status -ne "passed" -or $doctor.exit_code -ne 0 -or
    $doctor.version -ne $Version) {
    throw "Diagnostics JSON smoke report did not pass its versioned schema checks."
}

function Invoke-TargetSmoke {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $targetOutput = & $diagnosticsPath $Command @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostics $Command smoke command failed: $($Arguments -join ' ')"
    }

    return $targetOutput
}

if (-not [string]::IsNullOrWhiteSpace($WorkerPath)) {
    if (-not (Test-Path $WorkerPath -PathType Leaf)) {
        throw "Worker path for diagnostics smoke test does not exist: $WorkerPath"
    }

    Invoke-TargetSmoke -Command "worker" -Arguments @("--worker", $WorkerPath) | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($AppPath)) {
    if (-not (Test-Path $AppPath)) {
        throw "Application path for diagnostics smoke test does not exist: $AppPath"
    }

    Invoke-TargetSmoke -Command "app" -Arguments @("--app", $AppPath) | Out-Null
}

Write-Host "Windows diagnostics published and smoke-tested: $diagnosticsPath"
