<#
.SYNOPSIS
    Signs a built MSIX with a self-signed code-signing certificate and exports
    the public .cer next to it (so sideload installers can trust the package).

.DESCRIPTION
    Uses an existing "CN=TestCert" code-signing certificate from the current
    user's store (creating one if none exists) whose subject must match the
    package manifest's Publisher. Signs with signtool from the Windows SDK.

.EXAMPLE
    .\sign-release.ps1 -Msix packaging\MobileData.Package\AppPackages\MobileData.Package_1.0.2.0_x64_Test\MobileData.Package_1.0.2.0_x64.msix
#>

param(
    [Parameter(Mandatory = $true)][string]$Msix,
    [string]$Subject = "CN=TestCert"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Msix)) { throw "MSIX not found: $Msix" }

# Locate signtool (x64, newest SDK).
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin" `
    -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found (install Windows SDK)." }

# Find a usable code-signing cert, else create one valid 5 years.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and
                   ($_.EnhancedKeyUsageList.FriendlyName -contains 'Code Signing') } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed cert $Subject..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}
Write-Host "Signing with $($cert.Thumbprint) (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Green

# Sign (timestamp if a server is reachable, otherwise proceed without).
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /t http://timestamp.digicert.com $Msix
if ($LASTEXITCODE -ne 0) {
    Write-Host "Timestamping failed; signing without a timestamp." -ForegroundColor Yellow
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $Msix
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }
}
# Note: `verify /pa` reports an untrusted root for a self-signed cert until the
# .cer is imported (which Install.ps1 does on the target machine) — informational.
& $signtool.FullName verify /pa $Msix 2>&1 | Select-Object -Last 1 | Write-Host

# Export the public cert next to the package for the installer to trust.
$cerPath = [IO.Path]::ChangeExtension($Msix, ".cer")
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Write-Host "Signed. Certificate: $cerPath" -ForegroundColor Cyan
exit 0
