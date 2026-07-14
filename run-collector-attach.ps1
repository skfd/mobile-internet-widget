<#
.SYNOPSIS
    Hourly collector runner that scrapes usage via an attached, logged-in Chrome.

.DESCRIPTION
    Carrier login is gated by an invisible reCAPTCHA that blocks Playwright's
    automation browser, so we never log in from automation. Instead a dedicated
    Chrome profile stays signed in, and the collector attaches to it over CDP
    (port 9222) to reload the usage page each hour. reCAPTCHA only fires at
    login, so an already-authenticated session scrapes freely.

    This wrapper is self-healing: if that debug Chrome isn't running it launches
    it (minimized) using the persistent profile, which stays signed in across
    reboots until the carrier's session cookie eventually expires. When it does,
    the collector exits non-zero without writing, leaving the last good cache in
    place; sign in once in the debug Chrome and hourly scrapes resume.

.EXAMPLE
    .\run-collector-attach.ps1 -Provider fido
#>

param(
    [ValidateSet("fido", "rogers")]
    [string]$Provider = "fido"
)

$ErrorActionPreference = "Stop"

$chrome  = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$profile = "$env:USERPROFILE\.mobile-widget\chrome-attach"
$port    = 9222

function Test-DebugPort { [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }

# Ensure the logged-in debug Chrome is running (it holds the carrier session).
if (-not (Test-DebugPort)) {
    Start-Process $chrome -ArgumentList `
        "--remote-debugging-port=$port", "--user-data-dir=$profile", "--start-minimized"
    foreach ($i in 1..30) {
        Start-Sleep -Milliseconds 500
        if (Test-DebugPort) { break }
    }
    if (-not (Test-DebugPort)) {
        Write-Error "Debug Chrome did not open a listener on port $port."
        exit 4
    }
}

$exe = "$PSScriptRoot\src\MobileData.Collector\bin\Release\net8.0-windows\MobileData.Collector.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Collector exe not found at $exe (build Release first, or run install-collector-task.ps1)."
    exit 1
}

& $exe $Provider --attach
exit $LASTEXITCODE
