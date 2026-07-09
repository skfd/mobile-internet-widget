<#
.SYNOPSIS
    Rebuilds and deploys the Mobile Data Widget to the Windows 11 Widgets Board.

.DESCRIPTION
    Full cycle:
    1. Increments the package version and syncs the "Add widget" display name.
    2. Unregisters any existing package and stops widget processes (releases locks
       and clears cached widget metadata).
    3. Builds the MSIX packaging project with Visual Studio MSBuild.
    4. Copies images into the build layout and registers the package.

.PREREQUISITES
    - Visual Studio 2022/18 with "Windows application development" workload
    - Windows Developer Mode enabled
    - .NET 8 SDK

.NOTES
    Run from the repository root. After deploying, press Win+W and add the
    "Fido Data" widget.
#>

$ErrorActionPreference = "Stop"
Write-Host "=== Rebuild and Deploy Mobile Data Widget ===" -ForegroundColor Cyan

# 1. Increment version + sync the AppExtension display name.
$manifestPath = "$PSScriptRoot\packaging\MobileData.Package\Package.appxmanifest"
$content = Get-Content $manifestPath -Raw
if ($content -match '<Identity[^>]+Version="(\d+)\.(\d+)\.(\d+)\.(\d+)"') {
    $major = [int]$matches[1]; $minor = [int]$matches[2]; $build = [int]$matches[3] + 1; $revision = [int]$matches[4]
    $newVersion = "$major.$minor.$build.$revision"
    $content = $content -replace '<Identity([^>]+)Version="\d+\.\d+\.\d+\.\d+"', "<Identity`$1Version=""$newVersion"""
    $content = $content -replace '(DisplayName="Mobile Data Widget) v\d+(")', "`$1 v$build`$2"
    Set-Content -Path $manifestPath -Value $content -NoNewline
    Write-Host "Version -> $newVersion (Mobile Data Widget v$build)" -ForegroundColor Green
} else {
    Write-Host "ERROR: Could not find version in manifest" -ForegroundColor Red; exit 1
}

# 2. Unregister old package.
$oldPkg = Get-AppxPackage | Where-Object { $_.Name -eq "MobileDataWidget" }
if ($oldPkg) {
    Remove-AppxPackage -Package $oldPkg.PackageFullName
    Write-Host "Old package unregistered" -ForegroundColor Green
}

# Stop widget processes so cached metadata is cleared and files unlock.
foreach ($procName in @("MobileData.App", "WidgetService", "WidgetBoard")) {
    $attempt = 0
    while ($attempt -lt 5) {
        $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
        if ($procs) { $procs | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; $attempt++ }
        else { break }
    }
}
Start-Sleep -Seconds 2

# 3. Locate Visual Studio MSBuild.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { $vswhere = "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe" }
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) { Write-Host "ERROR: MSBuild not found" -ForegroundColor Red; exit 1 }
Write-Host "MSBuild: $msbuild" -ForegroundColor Green

Write-Host "`nBuilding MSIX package..." -ForegroundColor Yellow
& $msbuild "$PSScriptRoot\packaging\MobileData.Package\MobileData.Package.wapproj" /p:Configuration=Debug /p:Platform=x64 /restore /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Host "`nBuild failed!" -ForegroundColor Red; exit 1 }
Write-Host "Build succeeded!" -ForegroundColor Green

# 4. Copy images into the layout (not copied by the wapproj build) and register.
$imagesSrc = "$PSScriptRoot\packaging\MobileData.Package\Images"
$imagesDst = "$PSScriptRoot\packaging\MobileData.Package\bin\x64\Debug\Images"
New-Item -ItemType Directory -Force -Path $imagesDst | Out-Null
Copy-Item "$imagesSrc\*" -Destination $imagesDst -Recurse -Force
Write-Host "Images copied to layout" -ForegroundColor Green

$layoutManifest = "$PSScriptRoot\packaging\MobileData.Package\bin\x64\Debug\AppxManifest.xml"
if (-not (Test-Path $layoutManifest)) { Write-Host "ERROR: layout manifest not found" -ForegroundColor Red; exit 1 }
Add-AppxPackage -Path $layoutManifest -Register
Write-Host "`nDeploy succeeded!  Press Win+W and add the 'Fido Data' widget." -ForegroundColor Cyan
