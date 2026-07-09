# Clean script for Mobile Data Widget - removes bin and obj folders.

Write-Host "`n=== Cleaning bin and obj folders ===" -ForegroundColor Cyan

foreach ($folderName in @("bin", "obj")) {
    Write-Host "Searching for '$folderName' folders..." -ForegroundColor Yellow
    $folders = Get-ChildItem -Path "." -Filter $folderName -Recurse -Directory -ErrorAction SilentlyContinue
    if (-not $folders) {
        Write-Host "  None found." -ForegroundColor Green
    } else {
        foreach ($folder in $folders) {
            try {
                Remove-Item -Path $folder.FullName -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed: $($folder.FullName)" -ForegroundColor Green
            } catch {
                Write-Host "  Failed: $($folder.FullName) - $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
}

Write-Host "`nCleaning completed!" -ForegroundColor Green
