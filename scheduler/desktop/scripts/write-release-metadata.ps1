[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$")]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,
    [string]$WebView2Sha256,
    [string]$WebView2SourceUrl
)

$ErrorActionPreference = "Stop"

if ($env:GITHUB_ACTIONS -eq "true" -and [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
    throw "Release metadata requires GITHUB_SHA in GitHub Actions."
}

function Get-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd([char]92, [char]47) + [System.IO.Path]::DirectorySeparatorChar
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    if (-not $targetFullPath.StartsWith($baseFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release file is outside the publish directory: $TargetPath"
    }

    $relativePath = $targetFullPath.Substring($baseFullPath.Length).Replace([string][char]92, "/")
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        $relativePath.StartsWith("/") -or
        $relativePath.Split("/") -contains "..") {
        throw "Release file has an unsafe relative path: $TargetPath"
    }

    return $relativePath
}

$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDirectory = (Resolve-Path $PublishPath).Path
foreach ($relativePath in @("Scheduler.Desktop.exe", "Scheduler.Desktop.dll", "Scheduler.Diagnostics.exe", "SolverWorker.exe", "app.ico", "web\index.html", "sbom.cdx.json")) {
    if (-not (Test-Path (Join-Path $publishDirectory $relativePath) -PathType Leaf)) {
        throw "Publish directory is missing required release metadata input: $relativePath"
    }
}

$noticePath = Join-Path $publishDirectory "THIRD_PARTY_NOTICES.md"
$solverNotice = Get-Content -Raw (Join-Path $desktopRoot "native\SolverWorker\third_party\NOTICE.md")
$cadicalLicense = Get-Content -Raw (Join-Path $desktopRoot "native\SolverWorker\third_party\cadical\LICENSE")
$cakeLprLicense = Get-Content -Raw (Join-Path $desktopRoot "native\FormalVerification\third_party\cake_lpr\LICENSE")
$cakeLprProvenance = Get-Content -Raw (Join-Path $desktopRoot "native\FormalVerification\cake-lpr-provenance.json")
$noticeContent = @(
    "# Third-Party Notices"
    ""
    "This release statically links CaDiCaL 3.0.1."
    ""
    $solverNotice.TrimEnd()
    ""
    "## CaDiCaL MIT License"
    ""
    $cadicalLicense.TrimEnd()
    ""
    "## cake_lpr and CakeML License"
    ""
    "The optional formal UNSAT checker source is vendored at the pinned commit recorded in cake-lpr-provenance.json."
    ""
    $cakeLprLicense.TrimEnd()
    ""
    "cake_lpr provenance:"
    $cakeLprProvenance.TrimEnd()
    ""
    "## PdfPig"
    ""
    "The PDF timetable importer uses PdfPig 0.1.15, distributed under the Apache License 2.0."
    "Source: https://github.com/UglyToad/PdfPig/releases/tag/v0.1.15"
    "License: https://www.apache.org/licenses/LICENSE-2.0"
    ""
    "## DocumentFormat.OpenXml and System.IO.Packaging"
    ""
    "These packages are distributed under the MIT License."
    "Source: https://github.com/dotnet/Open-XML-SDK"
    "License: https://github.com/dotnet/Open-XML-SDK/blob/main/LICENSE.md"
    "System.IO.Packaging source: https://github.com/dotnet/runtime/tree/main/src/libraries/System.IO.Packaging"
    "The self-contained .NET Windows Desktop Runtime is distributed under the MIT License."
    "Runtime source and license: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"
    ""
    "## Microsoft WebView2"
    ""
    "The Microsoft.Web.WebView2 SDK is distributed under the MIT License."
    "Source: https://github.com/MicrosoftEdge/WebView2Feedback"
    "License: https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/LICENSE"
    "Any bundled WebView2 Runtime installer is Microsoft software and is subject to Microsoft's license terms."
) -join [Environment]::NewLine
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($noticePath, $noticeContent + [Environment]::NewLine, $utf8)

$manifest = [ordered]@{
    schema_version = 1
    application = "VNU-UET-Custom-Timetable-Scheduler"
    version = $Version
    source_revision = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { "uncommitted" } else { $env:GITHUB_SHA }
    created_utc = [DateTime]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    solver_worker = [ordered]@{
        file = "SolverWorker.exe"
        sha256 = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $publishDirectory "SolverWorker.exe")).Hash.ToLowerInvariant()
        cadical_version = "3.0.1"
    }
    diagnostics = [ordered]@{
        file = "Scheduler.Diagnostics.exe"
        sha256 = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $publishDirectory "Scheduler.Diagnostics.exe")).Hash.ToLowerInvariant()
    }
    webview2 = if ([string]::IsNullOrWhiteSpace($WebView2Sha256)) {
        [ordered]@{ bundled = $false }
    }
    else {
        [ordered]@{
            bundled = $true
            installer_file = "MicrosoftEdgeWebView2Setup.exe"
            sha256 = $WebView2Sha256.ToLowerInvariant()
            source_url = $WebView2SourceUrl
        }
    }
    publish_files = [ordered]@{}
}

Get-ChildItem -Path $publishDirectory -File -Recurse |
    Where-Object { $_.Name -ne "release-manifest.json" } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = Get-PortableRelativePath $publishDirectory $_.FullName
        $manifest.publish_files[$relativePath] = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
    }

$manifestPath = Join-Path $publishDirectory "release-manifest.json"
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine, $utf8)
Write-Host "Release metadata created: $manifestPath"
