<#
.SYNOPSIS
    Registers a Windows Scheduled Task that runs the usage collector every hour.

.DESCRIPTION
    Builds the collector (Release) and schedules it to run headless once an hour
    for the given carrier, keeping the widget's cache fresh. The task runs as the
    current user, only when you're logged on, so it can read your DPAPI-encrypted
    credentials and reuse the collector's persistent browser session.

.PREREQUISITES
    Run once first so a session/credentials exist:
        dotnet run --project src/MobileData.Collector -- fido --set-login
        dotnet run --project src/MobileData.Collector -- fido

.EXAMPLE
    .\install-collector-task.ps1                 # hourly Fido collector
    .\install-collector-task.ps1 -Provider fido -IntervalHours 1
    .\install-collector-task.ps1 -Uninstall      # remove the task
#>

param(
    [ValidateSet("fido", "rogers")]
    [string]$Provider = "fido",
    [int]$IntervalHours = 1,
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

# Build the collector to a stable exe path.
$proj = "$PSScriptRoot\src\MobileData.Collector\MobileData.Collector.csproj"
Write-Host "Building collector (Release)..." -ForegroundColor Yellow
dotnet build $proj -c Release -v minimal | Out-Null
$exe = "$PSScriptRoot\src\MobileData.Collector\bin\Release\net8.0-windows\MobileData.Collector.exe"
if (-not (Test-Path $exe)) { Write-Host "ERROR: collector exe not found at $exe" -ForegroundColor Red; exit 1 }

# Hourly trigger, repeating indefinitely, catching up missed runs.
$action  = New-ScheduledTaskAction -Execute $exe -Argument "$Provider --headless" -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(2) `
    -RepetitionInterval (New-TimeSpan -Hours $IntervalHours)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
# Run as the current user, only when logged on (needed for DPAPI + the browser).
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal `
    -Description "Fetch $Provider mobile data usage every $IntervalHours hour(s)" -Force | Out-Null

Write-Host "Installed task '$taskName' (every $IntervalHours h)." -ForegroundColor Green
Write-Host "Run now with:  Start-ScheduledTask -TaskName '$taskName'" -ForegroundColor Cyan
