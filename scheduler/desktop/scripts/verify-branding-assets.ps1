[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Get-RequiredFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Branding asset is missing: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-EqualHash {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$ActualPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $expected = (Get-FileHash -Algorithm SHA256 -LiteralPath (Get-RequiredFile $ExpectedPath)).Hash
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath (Get-RequiredFile $ActualPath)).Hash
    if ($expected -ne $actual) {
        throw "$Description differs from the canonical branding asset."
    }
}

function Get-PngSize {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes((Get-RequiredFile $Path))
    if ($bytes.Length -lt 24 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or
        $bytes[2] -ne 0x4E -or $bytes[3] -ne 0x47 -or $bytes[12] -ne 0x49 -or
        $bytes[13] -ne 0x48 -or $bytes[14] -ne 0x44 -or $bytes[15] -ne 0x52) {
        throw "Not a valid PNG with an IHDR chunk: $Path"
    }

    $width = ([int]$bytes[16] * 16777216) + ([int]$bytes[17] * 65536) + ([int]$bytes[18] * 256) + [int]$bytes[19]
    $height = ([int]$bytes[20] * 16777216) + ([int]$bytes[21] * 65536) + ([int]$bytes[22] * 256) + [int]$bytes[23]
    return [pscustomobject]@{ Width = $width; Height = $height }
}

function Get-IcoSizes {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes((Get-RequiredFile $Path))
    if ($bytes.Length -lt 6 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or
        $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
        throw "Not an icon resource: $Path"
    }

    $count = $bytes[4] -bor ($bytes[5] -shl 8)
    if ($count -le 0 -or $bytes.Length -lt (6 + (16 * $count))) {
        throw "Icon resource has no valid directory entries: $Path"
    }

    $sizes = [System.Collections.Generic.HashSet[int]]::new()
    for ($index = 0; $index -lt $count; $index++) {
        $offset = 6 + ($index * 16)
        $width = if ($bytes[$offset] -eq 0) { 256 } else { [int]$bytes[$offset] }
        $height = if ($bytes[$offset + 1] -eq 0) { 256 } else { [int]$bytes[$offset + 1] }
        if ($width -ne $height) {
            throw "Icon entry is not square in ${Path}: ${width}x${height}"
        }
        [void]$sizes.Add($width)
    }
    return $sizes
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\")).Path
$canonicalLogo = Join-Path $repoRoot "scheduler\branding\source\logo.png"
$landscape = Join-Path $repoRoot "scheduler\branding\generated\logo-landscape.png"
$iconMaster = Join-Path $repoRoot "scheduler\branding\generated\icon-master.png"
$appIcon = Join-Path $repoRoot "scheduler\branding\generated\app.ico"
$favicon = Join-Path $repoRoot "scheduler\branding\generated\favicon.ico"

Assert-EqualHash $canonicalLogo $landscape "Generated landscape logo"
Assert-EqualHash $canonicalLogo (Join-Path $repoRoot "scheduler\frontend\src\assets\logo.png") "Frontend logo"
Assert-EqualHash $canonicalLogo (Join-Path $repoRoot "scheduler\desktop\src\Scheduler.Desktop\Assets\logo.png") "WPF logo"
Assert-EqualHash $appIcon (Join-Path $repoRoot "scheduler\desktop\src\Scheduler.Desktop\Assets\app.ico") "WPF application icon"
Assert-EqualHash $appIcon (Join-Path $repoRoot "scheduler\desktop\src\Scheduler.Diagnostics\Assets\app.ico") "Diagnostics application icon"
Assert-EqualHash $favicon (Join-Path $repoRoot "scheduler\frontend\public\favicon.ico") "Frontend favicon"

$logoSize = Get-PngSize $canonicalLogo
if ($logoSize.Width -ne 253 -or $logoSize.Height -ne 193) {
    throw "Canonical logo dimensions changed: $($logoSize.Width)x$($logoSize.Height)"
}
$masterSize = Get-PngSize $iconMaster
if ($masterSize.Width -ne 512 -or $masterSize.Height -ne 512) {
    throw "Icon master must be 512x512: $($masterSize.Width)x$($masterSize.Height)"
}

$appSizes = Get-IcoSizes $appIcon
foreach ($requiredSize in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    if (-not $appSizes.Contains($requiredSize)) {
        throw "app.ico is missing a ${requiredSize}x${requiredSize} frame."
    }
}

$faviconSizes = Get-IcoSizes $favicon
foreach ($requiredSize in @(16, 32, 48)) {
    if (-not $faviconSizes.Contains($requiredSize)) {
        throw "favicon.ico is missing a ${requiredSize}x${requiredSize} frame."
    }
}

Write-Host "Branding assets verified: logo $($logoSize.Width)x$($logoSize.Height), app.ico frames $($appSizes -join ', '), favicon.ico frames $($faviconSizes -join ', ')."
