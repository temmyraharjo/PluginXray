[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$ProjectUrl,

    [Parameter(Mandatory = $true)]
    [string]$IconUrl,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseNotes,

    [string]$XrmToolBoxMinVersion = "1.2025.10.74",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [string]$ApiKey,
    [switch]$PackOnly
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\PluginDebugger\PluginDebugger.csproj"
$nuspec = Join-Path $root "PluginDebugger.nuspec"
$releaseOut = Join-Path $root "src\PluginDebugger\bin\Release\net48"
$artifacts = Join-Path $root "artifacts\nuget"

if (-not (Test-Path $project)) { throw "Project not found: $project" }
if (-not (Test-Path $nuspec)) { throw "Nuspec not found: $nuspec" }

$nuget = Get-Command nuget -ErrorAction SilentlyContinue
if (-not $nuget) {
    throw "nuget.exe is required. Install with: dotnet tool install --global NuGet.CommandLine"
}

Write-Host "Building Release..." -ForegroundColor Cyan
dotnet build $project -c Release -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $releaseOut "PluginDebugger.dll"
$runtimeDll = Join-Path $releaseOut "PluginDebugger.Runtime.dll"
if (-not (Test-Path $dll)) { throw "Missing output: $dll" }
if (-not (Test-Path $runtimeDll)) { throw "Missing output: $runtimeDll" }

$fileVersion = (Get-Item $dll).VersionInfo.FileVersion
if (-not $fileVersion.StartsWith($Version)) {
    throw "Package version ($Version) should match PluginDebugger assembly version ($fileVersion) for XrmToolBox Tool Library."
}

if (-not (Test-Path $artifacts)) {
    New-Item -ItemType Directory -Path $artifacts | Out-Null
}

Write-Host "Packing NuGet package..." -ForegroundColor Cyan
$properties = "version=$Version;projectUrl=$ProjectUrl;iconUrl=$IconUrl;releaseNotes=$ReleaseNotes;xrmToolBoxMinVersion=$XrmToolBoxMinVersion"
nuget pack $nuspec -OutputDirectory $artifacts -Properties $properties -NoDefaultExcludes
if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed." }

$pkg = Get-ChildItem -Path $artifacts -Filter "PluginDebugger.$Version*.nupkg" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $pkg) { throw "Could not find built package in $artifacts" }

Write-Host "Created package: $($pkg.FullName)" -ForegroundColor Green

if ($PackOnly) {
    Write-Host "PackOnly enabled. Skipping publish." -ForegroundColor Yellow
    return
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "ApiKey is required unless -PackOnly is used."
}

Write-Host "Publishing to $Source..." -ForegroundColor Cyan
nuget push $pkg.FullName -Source $Source -ApiKey $ApiKey -SkipDuplicate
if ($LASTEXITCODE -ne 0) { throw "NuGet push failed." }

Write-Host "Publish complete." -ForegroundColor Green
Write-Host "Next: register package id 'PluginDebugger' at https://www.xrmtoolbox.com/plugins/new/ once NuGet indexing completes." -ForegroundColor Cyan
