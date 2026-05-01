[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
chcp 65001 | Out-Null

$subject = "CN=AlliePack Test Signing"

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) }

if ($existing) {
    Write-Host "Re-using existing test certificate."
    $cert = $existing | Sort-Object NotAfter -Descending | Select-Object -First 1
} else {
    Write-Host "Creating self-signed code signing certificate..."
    $cert = New-SelfSignedCertificate `
        -Subject         $subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -Type            CodeSigningCert `
        -HashAlgorithm   SHA256 `
        -NotAfter        (Get-Date).AddYears(2)
}

Write-Host ""
Write-Host "  Subject    : $($cert.Subject)"
Write-Host "  Thumbprint : $($cert.Thumbprint)"
Write-Host "  Expires    : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ""
Write-Host "Use in allie-pack.yaml:"
Write-Host ""
Write-Host "  signing:"
Write-Host "    thumbprint: `"$($cert.Thumbprint)`""
