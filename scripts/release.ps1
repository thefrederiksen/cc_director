#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages CC Director for release.

.DESCRIPTION
    Publishes CC Director as a single-file executable. Framework-dependent by
    default (~5-10 MB, requires .NET 10 runtime). Pass -SelfContained for a
    standalone build (~150+ MB).

    Uses a three-step build to work around .NET 10 SDK bugs:
    1. Pre-build Core (avoids WPF _wpftmp stack overflow)
    2. Build WPF with RID (compiles XAML markup)
    3. MSBuild publish with NoBuild (avoids dotnet publish bundle size bug)

.PARAMETER SelfContained
    Build as self-contained (no .NET runtime required on target machine).

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    .\scripts\release.ps1
    .\scripts\release.ps1 -SelfContained
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\CcDirector.Wpf\CcDirector.Wpf.csproj"
$corePath = Join-Path $repoRoot "src\CcDirector.Core\CcDirector.Core.csproj"

# Read version from csproj
[xml]$csproj = Get-Content $projectPath
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    Write-Error "Could not read <Version> from $projectPath"
    exit 1
}

Write-Host "Building CC Director v$version ($Configuration)" -ForegroundColor Cyan

$selfContainedFlag = if ($SelfContained) { "true" } else { "false" }

if ($SelfContained) {
    Write-Host "  Mode: Self-contained" -ForegroundColor Yellow
} else {
    Write-Host "  Mode: Framework-dependent (.NET 10 runtime required)" -ForegroundColor Yellow
}

# Step 0: Clean
Write-Host "  Cleaning previous build..." -ForegroundColor Gray
& dotnet clean $projectPath -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet clean failed with exit code $LASTEXITCODE"
    exit 1
}

# Step 1: Pre-build Core project.
# Workaround: The .NET 10 WPF markup compiler (_wpftmp inner build) crashes with
# a stack overflow when it needs to build project references from clean state in
# the same MSBuild invocation. Building Core first avoids this.
Write-Host "  Pre-building Core dependency..." -ForegroundColor Gray
& dotnet build $corePath -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "Core pre-build failed with exit code $LASTEXITCODE"
    exit 1
}

# Step 2: Build WPF project with RID (compiles XAML markup)
Write-Host "  Building WPF project..." -ForegroundColor Gray
& dotnet build $projectPath -c $Configuration -r win-x64 --self-contained $selfContainedFlag --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "WPF build failed with exit code $LASTEXITCODE"
    exit 1
}

# Step 3: Publish using dotnet msbuild directly.
# Workaround: 'dotnet publish --no-build' has a bug that bundles the entire .NET
# runtime into the single-file even with SelfContained=false. Using 'dotnet msbuild
# -t:Publish' with NoBuild=true produces the correct framework-dependent bundle.
$msbuildArgs = @(
    "msbuild", $projectPath,
    "-t:Publish",
    "-p:Configuration=$Configuration",
    "-p:RuntimeIdentifier=win-x64",
    "-p:SelfContained=$selfContainedFlag",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:NoBuild=true"
)

if ($SelfContained) {
    $msbuildArgs += "-p:EnableCompressionInSingleFile=true"
}

Write-Host "  Publishing..." -ForegroundColor Gray
& dotnet @msbuildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit 1
}

# Locate published output
$publishDir = Join-Path $repoRoot "src\CcDirector.Wpf\bin\$Configuration\net10.0-windows\win-x64\publish"
$exePath = Join-Path $publishDir "cc_director.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Published exe not found at $exePath"
    exit 1
}

# Copy to releases directory
$releasesDir = Join-Path $repoRoot "releases"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}
$destPath = Join-Path $releasesDir "cc_director.exe"
Copy-Item $exePath $destPath -Force

$exeSize = (Get-Item $destPath).Length / 1MB
Write-Host ""
Write-Host "Build complete: $([math]::Round($exeSize, 1)) MB" -ForegroundColor Green
Write-Host "  $destPath" -ForegroundColor Green
