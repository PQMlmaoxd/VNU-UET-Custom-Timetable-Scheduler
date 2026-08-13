[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$")]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$DiagnosticsPath,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

function Get-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "$Description does not exist: $Path"
    }

    return (Resolve-Path $Path).Path
}

$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot "artifacts"))

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactRoot "release-assets"
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$artifactPrefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputFullPath.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be inside the desktop artifacts directory: $artifactRoot"
}

$publishDirectory = Get-RequiredFile (Join-Path ([System.IO.Path]::GetFullPath($PublishPath)) "Scheduler.Desktop.exe") "Published desktop executable"
$publishDirectory = Split-Path $publishDirectory -Parent
$installer = Get-RequiredFile $InstallerPath "Desktop installer"
$diagnostics = Get-RequiredFile $DiagnosticsPath "Standalone diagnostics executable"

foreach ($relativePath in @("release-manifest.json", "sbom.cdx.json", "THIRD_PARTY_NOTICES.md", "web\index.html")) {
    Get-RequiredFile (Join-Path $publishDirectory $relativePath) "Published release file $relativePath" | Out-Null
}

$expectedInstallerName = "VNU-UET-Custom-Timetable-Scheduler-$Version-Setup.exe"
if ([System.IO.Path]::GetFileName($installer) -ne $expectedInstallerName) {
    throw "Installer name does not match release version ${Version}: $installer"
}

$expectedDiagnosticsExecutableName = "Scheduler.Diagnostics.exe"
if (-not [string]::Equals(
        [System.IO.Path]::GetFileName($diagnostics),
        $expectedDiagnosticsExecutableName,
        [System.StringComparison]::Ordinal)) {
    throw "Diagnostics path must identify the exact $expectedDiagnosticsExecutableName file: $diagnostics"
}

Remove-Item -Recurse -Force $outputFullPath -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

$publishZip = Join-Path $outputFullPath "VNU-UET-Custom-Timetable-Scheduler-$Version-win-x64.zip"
$diagnosticsAssetName = "VNU-UET-Custom-Timetable-Scheduler-$Version-Diagnostics-win-x64.exe"
$diagnosticsAsset = Join-Path $outputFullPath $diagnosticsAssetName
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $publishZip -CompressionLevel Optimal
Copy-Item -Force $installer (Join-Path $outputFullPath $expectedInstallerName)
Copy-Item -Force $diagnostics $diagnosticsAsset

# Release metadata remains inside the self-contained publish ZIP. GitHub only
# publishes the installer, ZIP, and standalone diagnostics executable as release assets.

Write-Host "Release assets created: $outputFullPath"
