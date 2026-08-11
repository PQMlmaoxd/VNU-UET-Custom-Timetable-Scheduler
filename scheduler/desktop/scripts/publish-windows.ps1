[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SolverWorkerPath,
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$")]
    [string]$Version = "0.1.0-dev",
    [string]$WebRoot,
    [string]$OutputPath,
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$frontendRoot = (Resolve-Path (Join-Path $desktopRoot "..\frontend")).Path
$worker = (Resolve-Path $SolverWorkerPath).Path

if ([string]::IsNullOrWhiteSpace($WebRoot)) {
    $WebRoot = Join-Path $frontendRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $desktopRoot "artifacts\windows-x64"
}

$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot "artifacts"))
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$artifactPrefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputFullPath.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be inside the desktop artifacts directory: $artifactRoot"
}
$OutputPath = $outputFullPath

if (-not $SkipFrontendBuild) {
    Push-Location $frontendRoot
    try {
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
    }
    finally {
        Pop-Location
    }
}

$web = (Resolve-Path $WebRoot).Path
$webIndex = Join-Path $web "index.html"
if (-not (Test-Path $webIndex)) {
    throw "Web root must contain index.html: $web"
}

$runningDesktopProcesses = @(Get-Process -Name "Scheduler.Desktop" -ErrorAction SilentlyContinue)
if ($runningDesktopProcesses.Count -gt 0) {
    $processIds = ($runningDesktopProcesses | Select-Object -ExpandProperty Id) -join ", "
    throw "Close Scheduler.Desktop before publishing. Running process IDs: $processIds"
}

# The packaged WebView2 host opens index.html at the virtual-host root. Vite must
# therefore emit relative asset paths.
$webIndexContent = Get-Content -Raw $webIndex
if ($webIndexContent -match '(?i)(?:src|href)="/app/') {
    throw "Web bundle contains /app/-absolute assets and would render blank in the desktop host. Rebuild with Vite base './'."
}

Remove-Item -Recurse -Force $OutputPath -ErrorAction SilentlyContinue
& dotnet publish (Join-Path $desktopRoot "src\Scheduler.Desktop\Scheduler.Desktop.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    "-p:Version=$Version" `
    --output $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

Copy-Item -Recurse -Force $web (Join-Path $OutputPath "web")
Copy-Item -Force $worker (Join-Path $OutputPath "SolverWorker.exe")
$packagedWorker = Join-Path $OutputPath "SolverWorker.exe"
& $packagedWorker --version
if ($LASTEXITCODE -ne 0) { throw "Packaged SolverWorker version check failed." }

& $packagedWorker --self-test
if ($LASTEXITCODE -ne 0) { throw "Packaged SolverWorker self-test failed." }

& $packagedWorker --protocol-self-test
if ($LASTEXITCODE -ne 0) { throw "Packaged SolverWorker protocol self-test failed." }
