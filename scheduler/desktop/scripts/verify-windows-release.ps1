[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SolverWorkerPath,
    [string]$WebRoot,
    [string]$OutputPath,
    [string]$DiagnosticsOutputPath,
    [switch]$SkipFrontendInstall,
    [string]$ReleaseVersion,
    [switch]$BuildInstaller,
    [string]$InstallerVersion,
    [string]$WebView2InstallerPath,
    [string]$WebView2Sha256,
    [string]$WebView2SourceUrl,
    [string]$InnoSetupCompilerPath,
    [switch]$InstallerSmokeTest,
    [switch]$RequireExternalFixtures,
    [switch]$SkipExternalFixtureTests
)

$ErrorActionPreference = "Stop"

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

$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solutionPath = Join-Path $desktopRoot "Scheduler.sln"
$frontendRoot = (Resolve-Path (Join-Path $desktopRoot "..\frontend")).Path
$worker = (Resolve-Path $SolverWorkerPath).Path
$publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
$diagnosticsPublishScript = Join-Path $PSScriptRoot "publish-windows-diagnostics.ps1"
$sbomScript = Join-Path $PSScriptRoot "generate-sbom.ps1"
$metadataScript = Join-Path $PSScriptRoot "write-release-metadata.ps1"
$installerScript = Join-Path $PSScriptRoot "build-installer.ps1"

if ($BuildInstaller -and [string]::IsNullOrWhiteSpace($InstallerVersion)) {
    throw "InstallerVersion is required when BuildInstaller is enabled."
}

$effectiveReleaseVersion = if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    if ($BuildInstaller) { $InstallerVersion } else { "1.0.0-dev" }
}
else {
    $ReleaseVersion
}

if ($effectiveReleaseVersion -notmatch "^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$") {
    throw "ReleaseVersion must use the same version format as installer releases."
}

if ([string]::IsNullOrWhiteSpace($WebRoot)) {
    $WebRoot = Join-Path $frontendRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $desktopRoot "artifacts\windows-x64"
}

if ([string]::IsNullOrWhiteSpace($DiagnosticsOutputPath)) {
    $DiagnosticsOutputPath = Join-Path $desktopRoot "artifacts\diagnostics-windows-x64"
}

if (-not (Test-Path $worker -PathType Leaf)) {
    throw "SolverWorker.exe does not exist: $worker"
}

if ($RequireExternalFixtures) {
    foreach ($fixtureVariable in @("SCHEDULER_TEST_WORKBOOK", "SCHEDULER_TEST_PDF")) {
        $fixturePath = [Environment]::GetEnvironmentVariable($fixtureVariable)
        if ([string]::IsNullOrWhiteSpace($fixturePath) -or -not (Test-Path $fixturePath -PathType Leaf)) {
            throw "$fixtureVariable must identify an authorized external fixture when RequireExternalFixtures is enabled."
        }
    }
}

$previousWorker = $env:SCHEDULER_SOLVER_WORKER
$previousExternalWorker = $env:SCHEDULER_ALLOW_EXTERNAL_SOLVER
$env:SCHEDULER_SOLVER_WORKER = $worker
$env:SCHEDULER_ALLOW_EXTERNAL_SOLVER = "1"

try {
    Invoke-RequiredCommand { & $worker --version } "SolverWorker version check failed."
    Invoke-RequiredCommand { & $worker --self-test } "SolverWorker self-test failed."
    Invoke-RequiredCommand { & $worker --protocol-self-test } "SolverWorker protocol self-test failed."

    Push-Location $desktopRoot
    try {
        Invoke-RequiredCommand { & dotnet restore $solutionPath --locked-mode } "Desktop locked restore failed."
        Invoke-RequiredCommand {
            & dotnet build $solutionPath --configuration Release --no-restore
        } "Desktop Release build failed."
        Invoke-RequiredCommand {
            $testArguments = @($solutionPath, "--configuration", "Release", "--no-restore")
            if ($SkipExternalFixtureTests) {
                $testArguments += @("--filter", "Category!=Compatibility&Category!=ExternalFixture")
            }

            & dotnet test @testArguments
        } "Desktop Release tests failed."
    }
    finally {
        Pop-Location
    }

    Push-Location $frontendRoot
    try {
        if (-not $SkipFrontendInstall) {
            Invoke-RequiredCommand { & npm.cmd ci } "Frontend dependency installation failed."
        }

        Invoke-RequiredCommand { & npx.cmd playwright install chromium } "Playwright browser installation failed."
        Invoke-RequiredCommand { & npm.cmd test } "Frontend tests failed."
        Invoke-RequiredCommand { & npm.cmd run build } "Frontend production build failed."
        Invoke-RequiredCommand { & npm.cmd run test:e2e } "Frontend end-to-end tests failed."
    }
    finally {
        Pop-Location
    }

    & $publishScript `
        -SolverWorkerPath $worker `
        -Version $effectiveReleaseVersion `
        -WebRoot $WebRoot `
        -OutputPath $OutputPath `
        -SkipFrontendBuild
    if (-not $?) {
        throw "Windows publish-directory verification failed."
    }

    & $diagnosticsPublishScript `
        -Version $effectiveReleaseVersion `
        -OutputPath $DiagnosticsOutputPath `
        -WorkerPath $worker
    if (-not $?) {
        throw "Windows diagnostics publication failed."
    }

    $standaloneDiagnostics = Join-Path ([System.IO.Path]::GetFullPath($DiagnosticsOutputPath)) "Scheduler.Diagnostics.exe"
    $bundledDiagnostics = Join-Path ([System.IO.Path]::GetFullPath($OutputPath)) "Scheduler.Diagnostics.exe"
    Copy-Item -Force $standaloneDiagnostics $bundledDiagnostics

    & $sbomScript -Version $effectiveReleaseVersion -PublishPath $OutputPath
    if (-not $?) {
        throw "SBOM generation failed."
    }

    & $metadataScript -Version $effectiveReleaseVersion -PublishPath $OutputPath
    if (-not $?) {
        throw "Release metadata generation failed."
    }

    & $bundledDiagnostics app --app $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Final application diagnostics smoke test failed."
    }

    $standaloneDiagnosticsHash = (Get-FileHash -Algorithm SHA256 -Path $standaloneDiagnostics).Hash.ToLowerInvariant()
    $bundledDiagnosticsHash = (Get-FileHash -Algorithm SHA256 -Path $bundledDiagnostics).Hash.ToLowerInvariant()
    if ($standaloneDiagnosticsHash -ne $bundledDiagnosticsHash) {
        throw "Bundled Scheduler.Diagnostics.exe does not match the standalone release executable."
    }

    if ($BuildInstaller) {
        & $installerScript `
            -SolverWorkerPath $worker `
            -Version $InstallerVersion `
            -WebRoot $WebRoot `
            -PublishPath $OutputPath `
            -DiagnosticsPath $standaloneDiagnostics `
            -WebView2InstallerPath $WebView2InstallerPath `
            -WebView2Sha256 $WebView2Sha256 `
            -WebView2SourceUrl $WebView2SourceUrl `
            -InnoSetupCompilerPath $InnoSetupCompilerPath `
            -SkipFrontendBuild `
            -SkipPublish `
            -SkipSbom `
            -SmokeTest:$InstallerSmokeTest
        if (-not $?) {
            throw "Windows installer verification failed."
        }
    }
}
finally {
    $env:SCHEDULER_SOLVER_WORKER = $previousWorker
    $env:SCHEDULER_ALLOW_EXTERNAL_SOLVER = $previousExternalWorker
}

Write-Host "Release verification passed. Manual WebView2 interaction and clean-machine checks remain required."
