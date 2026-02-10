#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages CC Director for release.

.DESCRIPTION
    Publishes CC Director as a single-file executable. Framework-dependent by
    default (~5-10 MB, requires .NET 10 runtime). Pass -SelfContained for a
    standalone build (~150+ MB).

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

# Read version from csproj
[xml]$csproj = Get-Content $projectPath
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    Write-Error "Could not read <Version> from $projectPath"
    exit 1
}

Write-Host "Building CC Director v$version ($Configuration)" -ForegroundColor Cyan

# Build publish arguments
$publishArgs = @(
    "publish", $projectPath,
    "-c", $Configuration,
    "-r", "win-x64"
)

if ($SelfContained) {
    $publishArgs += "--self-contained", "true"
    $publishArgs += "-p:EnableCompressionInSingleFile=true"
    Write-Host "  Mode: Self-contained" -ForegroundColor Yellow
} else {
    $publishArgs += "--self-contained", "false"
    Write-Host "  Mode: Framework-dependent (.NET 10 runtime required)" -ForegroundColor Yellow
}

# Run dotnet publish
Write-Host "  Running: dotnet $($publishArgs -join ' ')" -ForegroundColor Gray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit 1
}

# Locate published output
$publishDir = Join-Path $repoRoot "src\CcDirector.Wpf\bin\$Configuration\net10.0-windows\win-x64\publish"
$exePath = Join-Path $publishDir "cc_director_v2.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Published exe not found at $exePath"
    exit 1
}

# Copy to releases directory
$releasesDir = Join-Path $repoRoot "releases"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}
$destPath = Join-Path $releasesDir "cc_director_v2.exe"
Copy-Item $exePath $destPath -Force

$exeSize = (Get-Item $destPath).Length / 1MB
Write-Host ""
Write-Host "Build complete: $([math]::Round($exeSize, 1)) MB" -ForegroundColor Green
Write-Host "  $destPath" -ForegroundColor Green
