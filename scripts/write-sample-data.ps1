<#
.SYNOPSIS
    Writes sample usage cache files so the widget can be exercised without the collector.
.DESCRIPTION
    Drops rogers.json and fido.json into %USERPROFILE%\.mobile-widget\ with
    realistic data so you can add the widgets and see them render before the
    Playwright collector has been tuned against your real carrier account.
#>

$dir = Join-Path $env:USERPROFILE ".mobile-widget"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$reset = (Get-Date).AddDays(14).ToString("yyyy-MM-ddTHH:mm:sszzz")
$now = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")

$rogers = [ordered]@{
    provider      = "rogers"
    planName      = "Rogers 5G 50GB"
    isUnlimited   = $false
    totalGb       = 50
    usedGb        = 32.4
    cycleResetsAt = $reset
    fetchedAt     = $now
}
$fido = [ordered]@{
    provider      = "fido"
    planName      = "Fido 20GB"
    isUnlimited   = $false
    totalGb       = 20
    usedGb        = 18.7
    cycleResetsAt = $reset
    fetchedAt     = $now
}

$rogers | ConvertTo-Json | Set-Content -Path (Join-Path $dir "rogers.json") -Encoding UTF8
$fido   | ConvertTo-Json | Set-Content -Path (Join-Path $dir "fido.json")   -Encoding UTF8

Write-Host "Wrote sample data to $dir" -ForegroundColor Green
Get-ChildItem $dir -Filter *.json | Select-Object Name, Length
