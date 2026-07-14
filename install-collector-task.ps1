<#
.SYNOPSIS
    Registers a Windows Scheduled Task that runs the usage collector every hour.

.DESCRIPTION
    Builds the collector (Release) and schedules run-collector-attach.ps1 once an
    hour for the given carrier, keeping the widget's cache fresh. The task runs as
    the current user, only when you're logged on, because it drives a logged-in
    Chrome session (carrier login is reCAPTCHA-gated and can't be automated).

.PREREQUISITES
    Sign in once in the dedicated debug Chrome so the session exists:
        & 'C:\Program Files\Google\Chrome\Application\chrome.exe' `
            --remote-debugging-port=9222 --user-data-dir="$env:USERPROFILE\.mobile-widget\chrome-attach"
    (sign into the carrier in that window), then confirm a scrape works:
        .\run-collector-attach.ps1 -Provider fido

.EXAMPLE
    .\install-collector-task.ps1                    # Fido collector every 20 min
    .\install-collector-task.ps1 -Provider fido -IntervalMinutes 20
    .\install-collector-task.ps1 -Uninstall         # remove the task
#>

param(
    [ValidateSet("fido", "rogers")]
    [string]$Provider = "fido",
    [int]$IntervalMinutes = 20,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$taskName = "MobileDataWidget-$Provider-Collector"

if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Removed task '$taskName'" -ForegroundColor Green
    } else {
        Write-Host "Task '$taskName' not found" -ForegroundColor Yellow
    }
    return
}

# Build the collector to a stable exe path (the wrapper invokes this exe).
$proj = "$PSScriptRoot\src\MobileData.Collector\MobileData.Collector.csproj"
Write-Host "Building collector (Release)..." -ForegroundColor Yellow
dotnet build $proj -c Release -v minimal | Out-Null
$exe = "$PSScriptRoot\src\MobileData.Collector\bin\Release\net8.0-windows\MobileData.Collector.exe"
if (-not (Test-Path $exe)) { Write-Host "ERROR: collector exe not found at $exe" -ForegroundColor Red; exit 1 }

# Repeating trigger, indefinite, catching up missed runs.
# Runs the wrapper, which ensures the logged-in debug Chrome is up, then scrapes.
$wrapper = "$PSScriptRoot\run-collector-attach.ps1"
$action  = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$wrapper`" -Provider $Provider" `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(2) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
# Run as the current user, only when logged on (needed for the browser session).
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal `
    -Description "Fetch $Provider mobile data usage every $IntervalMinutes min" -Force | Out-Null

Write-Host "Installed task '$taskName' (every $IntervalMinutes min)." -ForegroundColor Green
Write-Host "Run now with:  Start-ScheduledTask -TaskName '$taskName'" -ForegroundColor Cyan
