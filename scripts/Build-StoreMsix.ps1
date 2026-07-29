[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$PackageVersion,
    [switch]$SkipPublish,
    [switch]$KeepStaging
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$appProject = Join-Path $repoRoot "src\GpxView.App\GpxView.App.csproj"
$publishDir = Join-Path $artifactsRoot "publish\win-x64-store"
$manifestSource = Join-Path $repoRoot "installer\msix\Package.appxmanifest"
$assetSource = Join-Path $repoRoot "src\GpxView.App\Assets\Store"
$stagingDir = Join-Path $artifactsRoot "msix\staging"
$verifyDir = Join-Path $artifactsRoot "msix\verify"
$outputDir = Join-Path $artifactsRoot "store"

function Assert-ArtifactPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $fullPath"
    }
    return $fullPath
}

function Reset-ArtifactDirectory([string]$Path) {
    $safePath = Assert-ArtifactPath $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
    return $safePath
}

function Find-MakeAppx {
    if ($env:MAKEAPPX_EXE) {
        if (-not (Test-Path -LiteralPath $env:MAKEAPPX_EXE -PathType Leaf)) {
            throw "MAKEAPPX_EXE does not point to a file: $env:MAKEAPPX_EXE"
        }
        return [IO.Path]::GetFullPath($env:MAKEAPPX_EXE)
    }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $sdkBin = Join-Path $programFilesX86 "Windows Kits\10\bin"
    $candidate = Get-ChildItem -Path $sdkBin -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '[\\/]x64[\\/]makeappx\.exe$' } |
        Sort-Object { [Version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw "MakeAppx.exe was not found. Install the Windows 10 or Windows 11 SDK."
    }
    return $candidate.FullName
}

function Convert-ToPackageVersion([string]$Value) {
    try {
        $parsed = [Version]$Value
    }
    catch {
        throw "Invalid package version '$Value'. Use up to four numeric components."
    }

    $components = @(
        $parsed.Major,
        $parsed.Minor,
        $(if ($parsed.Build -ge 0) { $parsed.Build } else { 0 }),
        $(if ($parsed.Revision -ge 0) { $parsed.Revision } else { 0 })
    )
    if ($components.Where({ $_ -lt 0 -or $_ -gt 65535 }).Count -ne 0) {
        throw "MSIX version components must be between 0 and 65535: $Value"
    }
    return $components -join "."
}

if (-not $PackageVersion) {
    [xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
    $appVersion = $projectXml.Project.PropertyGroup.Version |
        Where-Object { $_ } |
        Select-Object -First 1
    if (-not $appVersion) {
        throw "The app project does not define Version."
    }
    $PackageVersion = [string]$appVersion
}
$PackageVersion = Convert-ToPackageVersion $PackageVersion

if (-not $SkipPublish) {
    & dotnet publish $appProject "-p:PublishProfile=win-x64-store" "-p:Configuration=$Configuration"
    if ($LASTEXITCODE -ne 0) {
        throw "Store payload publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $publishDir "GpxView.exe") -PathType Leaf)) {
    throw "Store payload is missing. Run without -SkipPublish to create it."
}
if (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object { $_.Name -ieq "MapServices.local.json" }) {
    throw "Store payload contains MapServices.local.json."
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Web\host.js") -PathType Leaf)) {
    throw "Store payload is missing Web\host.js."
}

$makeAppx = Find-MakeAppx
$stagingDir = Reset-ArtifactDirectory $stagingDir
$verifyDir = Reset-ArtifactDirectory $verifyDir
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$outputPath = Join-Path $outputDir "GpxView-$PackageVersion-win-x64.msix"

try {
    Get-ChildItem -LiteralPath $publishDir -Force |
        Copy-Item -Destination $stagingDir -Recurse -Force

    $stagingAssets = Join-Path $stagingDir "Assets"
    New-Item -ItemType Directory -Path $stagingAssets -Force | Out-Null
    @("Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png") |
        ForEach-Object { Copy-Item -LiteralPath (Join-Path $assetSource $_) -Destination $stagingAssets -Force }

    $stagingManifest = Join-Path $stagingDir "AppxManifest.xml"
    Copy-Item -LiteralPath $manifestSource -Destination $stagingManifest -Force
    [xml]$manifestXml = Get-Content -LiteralPath $stagingManifest -Raw
    $manifestXml.Package.Identity.SetAttribute("Version", $PackageVersion)
    $manifestXml.Save($stagingManifest)

    $packOutput = & $makeAppx pack /d $stagingDir /p $outputPath /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        $packOutput | Write-Host
        throw "MakeAppx pack failed with exit code $LASTEXITCODE."
    }

    $unpackOutput = & $makeAppx unpack /p $outputPath /d $verifyDir /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        $unpackOutput | Write-Host
        throw "MakeAppx verification unpack failed with exit code $LASTEXITCODE."
    }

    [xml]$packedManifest = Get-Content -LiteralPath (Join-Path $verifyDir "AppxManifest.xml") -Raw
    $identity = $packedManifest.Package.Identity
    if ($identity.Name -ne "SuDan.GpxView" -or
        $identity.Publisher -ne "CN=DBB8CB7C-AA92-4365-B28B-709FB95AB14B" -or
        $identity.Version -ne $PackageVersion -or
        $identity.ProcessorArchitecture -ne "x64") {
        throw "Packed MSIX identity does not match the Partner Center product."
    }
    if (Get-ChildItem -LiteralPath $verifyDir -Recurse -File | Where-Object { $_.Name -ieq "MapServices.local.json" }) {
        throw "Packed MSIX contains MapServices.local.json."
    }

    $sizeMiB = [Math]::Round((Get-Item -LiteralPath $outputPath).Length / 1MB, 1)
    Write-Host "Created Store upload package: $outputPath ($sizeMiB MiB)"
    Write-Host "Identity: SuDan.GpxView / $PackageVersion / x64"
    Write-Host "The package is intentionally unsigned; Microsoft Store signs it after submission."
}
finally {
    if (Test-Path -LiteralPath $verifyDir) {
        Remove-Item -LiteralPath (Assert-ArtifactPath $verifyDir) -Recurse -Force
    }
    if (-not $KeepStaging -and (Test-Path -LiteralPath $stagingDir)) {
        Remove-Item -LiteralPath (Assert-ArtifactPath $stagingDir) -Recurse -Force
    }
}
