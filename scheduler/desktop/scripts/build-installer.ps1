[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SolverWorkerPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$")]
    [string]$Version,
    [string]$WebRoot,
    [string]$PublishPath,
    [string]$OutputPath,
    [string]$WebView2InstallerPath,
    [string]$WebView2Sha256,
    [string]$WebView2SourceUrl,
    [string]$InnoSetupCompilerPath,
    [switch]$SkipFrontendBuild,
    [switch]$SkipPublish,
    [switch]$SkipSbom,
    [switch]$SmokeTest
)

$ErrorActionPreference = "Stop"

function Get-RequiredPath {
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

function Invoke-RequiredCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Test-WebView2Input {
    param(
        [string]$InstallerPath,
        [string]$Sha256,
        [string]$SourceUrl
    )

    $provided = @(
        @($InstallerPath, $Sha256, $SourceUrl) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    ).Count
    if ($provided -eq 0) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($InstallerPath) -or
        [string]::IsNullOrWhiteSpace($Sha256) -or
        [string]::IsNullOrWhiteSpace($SourceUrl)) {
        throw "WebView2InstallerPath, WebView2Sha256, and WebView2SourceUrl must be supplied together."
    }

    if ($Sha256 -notmatch "^[A-Fa-f0-9]{64}$") {
        throw "WebView2Sha256 must be a SHA-256 hex digest."
    }

    $sourceUri = [Uri]$SourceUrl
    if (-not $sourceUri.IsAbsoluteUri -or $sourceUri.Scheme -ne "https" -or
        $sourceUri.Host -notmatch "(^|\.)microsoft\.com$") {
        throw "WebView2SourceUrl must be an official Microsoft HTTPS URL."
    }

    $runtime = Get-RequiredPath $InstallerPath "WebView2 installer"
    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $runtime).Hash.ToLowerInvariant()
    if ($actualHash -ne $Sha256.ToLowerInvariant()) {
        throw "WebView2 installer SHA-256 does not match the supplied value."
    }

    $signature = Get-AuthenticodeSignature -FilePath $runtime
    if ($signature.Status -ne "Valid" -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch "Microsoft") {
        throw "WebView2 installer must have a valid Microsoft Authenticode signature."
    }

    return [pscustomobject]@{
        Path = $runtime
        Sha256 = $actualHash
        SourceUrl = $sourceUri.AbsoluteUri
    }
}

$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
$sbomScript = Join-Path $PSScriptRoot "generate-sbom.ps1"
$metadataScript = Join-Path $PSScriptRoot "write-release-metadata.ps1"
$installerScript = Join-Path $desktopRoot "installer\SchedulerDesktop.iss"
$worker = Get-RequiredPath $SolverWorkerPath "SolverWorker.exe"
$runtime = Test-WebView2Input $WebView2InstallerPath $WebView2Sha256 $WebView2SourceUrl

if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path $desktopRoot "artifacts\windows-x64"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $desktopRoot "artifacts\installer"
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompilerPath)) {
    $InnoSetupCompilerPath = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
}

$iscc = Get-RequiredPath $InnoSetupCompilerPath "Inno Setup compiler"

if (-not $SkipPublish) {
    & $publishScript `
        -SolverWorkerPath $worker `
        -Version $Version `
        -WebRoot $WebRoot `
        -OutputPath $PublishPath `
        -SkipFrontendBuild:$SkipFrontendBuild
    if (-not $?) {
        throw "Windows publish directory creation failed."
    }
}

$publishDirectory = (Resolve-Path $PublishPath).Path
$requiredPublishFiles = @(
    "Scheduler.Desktop.exe",
    "Scheduler.Desktop.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "SolverWorker.exe",
    "app.ico",
    "web\index.html"
)
foreach ($relativePath in $requiredPublishFiles) {
    if (-not (Test-Path (Join-Path $publishDirectory $relativePath) -PathType Leaf)) {
        throw "Publish directory is missing required release input: $relativePath"
    }
}

$publishedWorkerHash = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $publishDirectory "SolverWorker.exe")).Hash.ToLowerInvariant()
$inputWorkerHash = (Get-FileHash -Algorithm SHA256 -Path $worker).Hash.ToLowerInvariant()
if ($publishedWorkerHash -ne $inputWorkerHash) {
    throw "Publish directory SolverWorker.exe does not match SolverWorkerPath."
}

$assemblyPath = Join-Path $publishDirectory "Scheduler.Desktop.dll"
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString(3)
$expectedAssemblyVersion = ($Version -split "-")[0]
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "Published application version $assemblyVersion does not match requested version $expectedAssemblyVersion."
}

if (-not $SkipSbom -or $null -ne $runtime) {
    & $sbomScript `
        -Version $Version `
        -PublishPath $publishDirectory `
        -WebView2InstallerPath $(if ($null -eq $runtime) { $null } else { $runtime.Path }) `
        -WebView2Sha256 $(if ($null -eq $runtime) { $null } else { $runtime.Sha256 }) `
        -WebView2SourceUrl $(if ($null -eq $runtime) { $null } else { $runtime.SourceUrl })
    if (-not $?) {
        throw "SBOM generation failed."
    }
}

if (-not (Test-Path (Join-Path $publishDirectory "sbom.cdx.json") -PathType Leaf)) {
    throw "Publish directory is missing sbom.cdx.json."
}

$outputDirectory = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

& $metadataScript `
    -Version $Version `
    -PublishPath $publishDirectory `
    -WebView2Sha256 $(if ($null -eq $runtime) { $null } else { $runtime.Sha256 }) `
    -WebView2SourceUrl $(if ($null -eq $runtime) { $null } else { $runtime.SourceUrl })
if (-not $?) {
    throw "Release metadata generation failed."
}

$compilerArguments = @(
    "/Qp"
    "/DAppVersion=$Version"
    "/DSourceDir=$publishDirectory"
    "/DOutputDir=$outputDirectory"
    "/DIncludeWebView2=$([int]($null -ne $runtime))"
)

if ($null -ne $runtime) {
    $compilerArguments += "/DWebView2Installer=$($runtime.Path)"
}

$compilerArguments += $installerScript
Invoke-RequiredCommand { & $iscc @compilerArguments } "Inno Setup compilation failed."

$installerPath = Join-Path $outputDirectory "VNU-UET-Custom-Timetable-Scheduler-$Version-Setup.exe"
if (-not (Test-Path $installerPath -PathType Leaf)) {
    throw "Inno Setup did not produce the expected installer: $installerPath"
}

if ($SmokeTest) {
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("VNU-UET-Custom-Timetable-Scheduler-smoke-" + [Guid]::NewGuid().ToString("N"))
    try {
        $installProcess = Start-Process `
            -FilePath $installerPath `
            -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=$smokeRoot") `
            -Wait `
            -PassThru
        if ($installProcess.ExitCode -ne 0) {
            throw "Installer smoke installation failed with exit code $($installProcess.ExitCode)."
        }

        foreach ($relativePath in @("Scheduler.Desktop.exe", "SolverWorker.exe", "app.ico", "web\index.html", "THIRD_PARTY_NOTICES.md", "release-manifest.json", "sbom.cdx.json")) {
            if (-not (Test-Path (Join-Path $smokeRoot $relativePath) -PathType Leaf)) {
                throw "Installer smoke check is missing: $relativePath"
            }
        }

        $installedWorker = Join-Path $smokeRoot "SolverWorker.exe"
        & $installedWorker --self-test
        if ($LASTEXITCODE -ne 0) {
            throw "Installed SolverWorker self-test failed."
        }

        $uninstaller = Join-Path $smokeRoot "unins000.exe"
        $uninstallProcess = Start-Process `
            -FilePath $uninstaller `
            -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") `
            -Wait `
            -PassThru
        if ($uninstallProcess.ExitCode -ne 0) {
            throw "Installer smoke uninstall failed with exit code $($uninstallProcess.ExitCode)."
        }
        if (Test-Path $smokeRoot) {
            throw "Installer smoke uninstall left its install directory behind: $smokeRoot"
        }
    }
    finally {
        Remove-Item -Recurse -Force $smokeRoot -ErrorAction SilentlyContinue
    }
}

Write-Host "Installer created: $installerPath"
if ($null -eq $runtime) {
    Write-Warning "This installer requires an existing WebView2 Runtime. Supply pinned WebView2 inputs for a self-contained runtime installation path."
}
