[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$")]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,
    [string]$OutputPath,
    [string]$WebView2InstallerPath,
    [string]$WebView2Sha256,
    [string]$WebView2SourceUrl
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

function ConvertTo-PurlName {
    param([Parameter(Mandatory = $true)][string]$Name)

    return [Uri]::EscapeDataString($Name).Replace("%2F", "/")
}

function Get-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd("\\") + "\\"
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace("/", "\\")
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][string]$Text)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function New-Component {
    param(
        [Parameter(Mandatory = $true)][string]$Type,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Purl,
        [hashtable]$Properties,
        [string]$LicenseName,
        [string]$LicenseUrl
    )

    $component = [ordered]@{
        type = $Type
        'bom-ref' = $Purl
        name = $Name
        version = $Version
        purl = $Purl
    }

    if (-not [string]::IsNullOrWhiteSpace($LicenseName)) {
        $license = [ordered]@{ name = $LicenseName }
        if (-not [string]::IsNullOrWhiteSpace($LicenseUrl)) {
            $license.url = $LicenseUrl
        }
        $component.licenses = @([ordered]@{ license = $license })
    }

    if ($null -ne $Properties -and $Properties.Count -gt 0) {
        $component.properties = @(
            $Properties.GetEnumerator() |
                Sort-Object Name |
                ForEach-Object {
                    [ordered]@{
                        name = $_.Key
                        value = [string]$_.Value
                    }
                }
        )
    }

    return [pscustomobject]$component
}

$nugetLicenses = @{
    "DocumentFormat.OpenXml" = @{ Name = "MIT"; Url = "https://github.com/dotnet/Open-XML-SDK/blob/main/LICENSE.md" }
    "DocumentFormat.OpenXml.Framework" = @{ Name = "MIT"; Url = "https://github.com/dotnet/Open-XML-SDK/blob/main/LICENSE.md" }
    "Microsoft.Web.WebView2" = @{ Name = "MIT"; Url = "https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/LICENSE" }
    "PdfPig" = @{ Name = "Apache-2.0"; Url = "https://www.apache.org/licenses/LICENSE-2.0" }
    "System.IO.Packaging" = @{ Name = "MIT"; Url = "https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Packaging/LICENSE.txt" }
}

function Get-PackageNameFromLockPath {
    param([Parameter(Mandatory = $true)][string]$LockPath)

    $parts = $LockPath -split "node_modules/"
    return $parts[$parts.Length - 1]
}

$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$workspaceRoot = (Resolve-Path (Join-Path $desktopRoot "..\..")).Path
$desktopProject = Join-Path $desktopRoot "src\Scheduler.Desktop\Scheduler.Desktop.csproj"
$diagnosticsProject = Join-Path $desktopRoot "src\Scheduler.Diagnostics\Scheduler.Diagnostics.csproj"
$frontendLock = Join-Path $workspaceRoot "scheduler\frontend\package-lock.json"
$cadicalProvenance = Join-Path $desktopRoot "native\SolverWorker\third_party\cadical-provenance.json"
$cakeLprProvenance = Join-Path $desktopRoot "native\FormalVerification\cake-lpr-provenance.json"

$publishDirectory = (Resolve-Path $PublishPath).Path
foreach ($requiredFile in @("Scheduler.Desktop.exe", "Scheduler.Diagnostics.exe")) {
    if (-not (Test-Path (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "PublishPath is missing required release file: $requiredFile"
    }
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $publishDirectory "sbom.cdx.json"
}

$frontendLock = Get-RequiredPath $frontendLock "Frontend package lock"
$cadicalProvenance = Get-RequiredPath $cadicalProvenance "CaDiCaL provenance"
$cakeLprProvenance = Get-RequiredPath $cakeLprProvenance "CakeLPR provenance"
$diagnosticsProject = Get-RequiredPath $diagnosticsProject "Diagnostics project"
$componentsByReference = @{}

$packageProjects = @($desktopProject, $diagnosticsProject)
foreach ($packageProject in $packageProjects) {
    $packageReportPath = [System.IO.Path]::GetTempFileName()
    try {
        & dotnet list $packageProject package --include-transitive --format json *> $packageReportPath
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to inspect resolved .NET packages. Run dotnet restore before generating the SBOM."
        }

        $packageReport = Get-Content -Raw $packageReportPath | ConvertFrom-Json
        foreach ($project in $packageReport.projects) {
            foreach ($framework in $project.frameworks) {
                foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                    if ($null -eq $package) {
                        continue
                    }

                    $name = [string]$package.id
                    $resolvedVersion = [string]$package.resolvedVersion
                    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($resolvedVersion)) {
                        throw "Resolved .NET package report contains an incomplete package entry."
                    }

                    if (-not $nugetLicenses.ContainsKey($name)) {
                        throw "No license metadata is defined for resolved NuGet package '$name'."
                    }

                    $license = $nugetLicenses[$name]

                    $purl = "pkg:nuget/$([Uri]::EscapeDataString($name).ToLowerInvariant())@$resolvedVersion"
                    $componentsByReference[$purl] = New-Component `
                        -Type "library" `
                        -Name $name `
                        -Version $resolvedVersion `
                        -Purl $purl `
                        -Properties @{ "scheduler:source" = "nuget"; "scheduler:license-url" = $license.Url } `
                        -LicenseName $license.Name `
                        -LicenseUrl $license.Url
                }
            }
        }
    }
    finally {
        Remove-Item -Force $packageReportPath -ErrorAction SilentlyContinue
    }
}

# Windows PowerShell cannot deserialize the npm root package because its key is
# intentionally the empty string. JavaScriptSerializer preserves that key.
Add-Type -AssemblyName System.Web.Extensions
$jsonSerializer = [System.Web.Script.Serialization.JavaScriptSerializer]::new()
$jsonSerializer.MaxJsonLength = [int]::MaxValue
$npmLock = $jsonSerializer.DeserializeObject((Get-Content -Raw $frontendLock))
if ($npmLock["lockfileVersion"] -ne 3) {
    throw "Only npm lockfile version 3 is supported; found $($npmLock["lockfileVersion"])."
}

foreach ($entry in $npmLock["packages"].GetEnumerator()) {
    if ([string]::IsNullOrEmpty([string]$entry.Key)) {
        continue
    }

    $package = $entry.Value
    $name = Get-PackageNameFromLockPath ([string]$entry.Key)
    $resolvedVersion = [string]$package["version"]
    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($resolvedVersion)) {
        throw "Frontend lockfile contains an incomplete package entry: $($entry.Key)"
    }

    $purl = "pkg:npm/$(ConvertTo-PurlName $name)@$resolvedVersion"
    $properties = @{ "scheduler:source" = "npm" }
    if ($package["dev"] -eq $true) {
        $properties["scheduler:dependency-scope"] = "development"
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$package["resolved"])) {
        $properties["npm:resolved"] = [string]$package["resolved"]
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$package["integrity"])) {
        $properties["npm:integrity"] = [string]$package["integrity"]
    }

    $licenseName = if ([string]::IsNullOrWhiteSpace([string]$package["license"])) {
        $null
    }
    else {
        [string]$package["license"]
    }
    $componentsByReference[$purl] = New-Component `
        -Type "library" `
        -Name $name `
        -Version $resolvedVersion `
        -Purl $purl `
        -Properties $properties `
        -LicenseName $licenseName
}

$cadical = Get-Content -Raw $cadicalProvenance | ConvertFrom-Json
$cadicalPurl = "pkg:generic/cadical@$($cadical.version)?commit=$($cadical.commit)"
$componentsByReference[$cadicalPurl] = New-Component `
    -Type "library" `
    -Name $cadical.name `
    -Version $cadical.version `
    -Purl $cadicalPurl `
    -Properties @{
        "scheduler:archive-sha256" = $cadical.archive_sha256
        "scheduler:source-url" = $cadical.archive_url
        "scheduler:source" = "vendored"
        "scheduler:tag" = $cadical.tag
    } `
    -LicenseName $cadical.license `
    -LicenseUrl "https://github.com/arminbiere/cadical/blob/rel-3.0.1/LICENSE"

$cakeLpr = Get-Content -Raw $cakeLprProvenance | ConvertFrom-Json
$cakeLprPurl = "pkg:generic/cake_lpr@source-$($cakeLpr.commit)"
$componentsByReference[$cakeLprPurl] = New-Component `
    -Type "library" `
    -Name $cakeLpr.name `
    -Version "source-$($cakeLpr.commit.Substring(0, 12))" `
    -Purl $cakeLprPurl `
    -Properties @{
        "scheduler:source" = "vendored-formal-verification-tooling"
        "scheduler:dependency-scope" = "optional-formal-verification"
        "scheduler:source-url" = $cakeLpr.repository
        "scheduler:commit" = $cakeLpr.commit
        "scheduler:hol4-commit" = $cakeLpr.hol4_commit
        "scheduler:cakeml-commit" = $cakeLpr.cakeml_commit
    } `
    -LicenseName $cakeLpr.license `
    -LicenseUrl "https://github.com/tanyongkiam/cake_lpr/blob/$($cakeLpr.commit)/LICENSE"

if (-not [string]::IsNullOrWhiteSpace($WebView2InstallerPath)) {
    $webView2Path = Get-RequiredPath $WebView2InstallerPath "WebView2 installer"
    if ([string]::IsNullOrWhiteSpace($WebView2Sha256) -or
        $WebView2Sha256 -notmatch "^[A-Fa-f0-9]{64}$" -or
        [string]::IsNullOrWhiteSpace($WebView2SourceUrl)) {
        throw "WebView2 installer metadata is incomplete for the SBOM."
    }

    $webView2Hash = (Get-FileHash -Algorithm SHA256 -Path $webView2Path).Hash.ToLowerInvariant()
    if ($webView2Hash -ne $WebView2Sha256.ToLowerInvariant()) {
        throw "WebView2 installer SHA-256 does not match the SBOM input."
    }

    $webView2Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($webView2Path).ProductVersion
    if ([string]::IsNullOrWhiteSpace($webView2Version)) {
        $webView2Version = "standalone"
    }
    $webView2Purl = "pkg:generic/microsoft-edge-webview2-evergreen-standalone-runtime@$([Uri]::EscapeDataString($webView2Version))"
    $componentsByReference[$webView2Purl] = New-Component `
        -Type "framework" `
        -Name "Microsoft Edge WebView2 Evergreen Standalone Runtime" `
        -Version $webView2Version `
        -Purl $webView2Purl `
        -Properties @{ "scheduler:source" = "release-input"; "scheduler:sha256" = $webView2Hash; "scheduler:source-url" = $WebView2SourceUrl } `
        -LicenseName "Microsoft runtime license" `
        -LicenseUrl "https://www.microsoft.com/en-us/servicesagreement"
}

$webIndexPath = Join-Path $publishDirectory "web\index.html"
if (-not (Test-Path $webIndexPath -PathType Leaf)) {
    throw "PublishPath is missing the packaged frontend entry point: web\index.html"
}
$webIndexHash = (Get-FileHash -Algorithm SHA256 -Path $webIndexPath).Hash.ToLowerInvariant()
$publishRecords = @(
    Get-ChildItem -Path $publishDirectory -File -Recurse |
        Where-Object { $_.Name -notin @("sbom.cdx.json", "release-manifest.json", "THIRD_PARTY_NOTICES.md") } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = (Get-PortableRelativePath $publishDirectory $_.FullName).Replace("\", "/")
            "$((Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant())  $relativePath"
        }
)
$publishTreeHash = Get-Sha256Text (($publishRecords -join "`n") + "`n")
$hostFxrPath = Join-Path $publishDirectory "hostfxr.dll"
if (-not (Test-Path $hostFxrPath -PathType Leaf)) {
    throw "PublishPath is missing hostfxr.dll from the self-contained .NET runtime."
}
$runtimeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostFxrPath).ProductVersion
if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
    throw "Unable to determine the bundled .NET runtime version."
}
$runtimePurl = "pkg:generic/dotnet-windowsdesktop-runtime@$([Uri]::EscapeDataString($runtimeVersion))"
$componentsByReference[$runtimePurl] = New-Component `
    -Type "framework" `
    -Name "Bundled .NET Windows Desktop Runtime" `
    -Version $runtimeVersion `
    -Purl $runtimePurl `
    -Properties @{ "scheduler:source" = "self-contained-publish"; "scheduler:hostfxr-sha256" = (Get-FileHash -Algorithm SHA256 -Path $hostFxrPath).Hash.ToLowerInvariant() } `
    -LicenseName "MIT" `
    -LicenseUrl "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"
$applicationPurl = "pkg:generic/vnu-uet-custom-timetable-scheduler@$Version"
$diagnosticsPath = Join-Path $publishDirectory "Scheduler.Diagnostics.exe"
$diagnosticsPurl = "pkg:generic/vnu-uet-custom-timetable-scheduler-diagnostics@$Version"
$componentsByReference[$diagnosticsPurl] = New-Component `
    -Type "application" `
    -Name "VNU-UET Custom Timetable Scheduler Diagnostics" `
    -Version $Version `
    -Purl $diagnosticsPurl `
    -Properties @{
        "scheduler:file" = "Scheduler.Diagnostics.exe"
        "scheduler:role" = "bundled-tester-cli"
        "scheduler:sha256" = (Get-FileHash -Algorithm SHA256 -Path $diagnosticsPath).Hash.ToLowerInvariant()
    }
$applicationProperties = [ordered]@{
    "scheduler:source" = "desktop-publish"
    "scheduler:publish-file-count" = [string]$publishRecords.Count
    "scheduler:publish-tree-sha256" = $publishTreeHash
    "scheduler:publish-tree-excludes" = "sbom.cdx.json,release-manifest.json,THIRD_PARTY_NOTICES.md"
    "scheduler:web-index-sha256" = $webIndexHash
    "scheduler:web-root" = "web"
}

$sbom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    version = 1
    metadata = [ordered]@{
        component = [ordered]@{
            type = "application"
            'bom-ref' = "pkg:generic/vnu-uet-custom-timetable-scheduler@$Version"
            name = "VNU-UET-Custom-Timetable-Scheduler"
            version = $Version
            purl = $applicationPurl
            properties = @(
                $applicationProperties.GetEnumerator() |
                    Sort-Object Name |
                    ForEach-Object {
                        [ordered]@{ name = $_.Key; value = [string]$_.Value }
                    }
            )
        }
        tools = @([ordered]@{
                vendor = "Scheduler"
                name = "generate-sbom.ps1"
                version = "1"
            })
    }
    components = @($componentsByReference.Values | Sort-Object { $_.'bom-ref' })
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$sbomJson = $sbom | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($OutputPath, $sbomJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "SBOM created: $OutputPath ($($componentsByReference.Count) components)"
