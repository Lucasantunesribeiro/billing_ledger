param(
    [Parameter(Mandatory=$true)]
    [string]$InvoiceId,
    [string]$ExternalPaymentId = $([guid]::NewGuid().ToString().Substring(0,8)),
    [decimal]$Amount = 350.00,
    [string]$Secret = "test-secret",
    [string]$BaseUrl = "http://localhost:5000"
)

$body = @"
{"invoiceId":"$InvoiceId","externalPaymentId":"pix-$ExternalPaymentId","provider":"PIX","amount":$Amount}
"@

# Calcula o HMAC-SHA256 da string exata do Body
$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($Secret))
$hashBytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($body))
$signature = [BitConverter]::ToString($hashBytes).Replace("-", "").ToLower()

Write-Host "Enviando payload para a Invoce: $InvoiceId"
Write-Host "Payload gerado:`n$body`n"
Write-Host "Assinatura HMAC calculada: sha256=$signature`n"

try {
    $response = Invoke-RestMethod -Uri "$BaseUrl/api/payments/webhook" `
        -Method POST `
        -Body $body `
        -ContentType "application/json" `
        -Headers @{ "X-Webhook-Signature" = "sha256=$signature" }
    
    Write-Host "Sucesso!" -ForegroundColor Green
    $response | ConvertTo-Json
} catch {
    Write-Host "Erro durante a chamada do Webhook:" -ForegroundColor Red
    $_.Exception.Message
    $_.ErrorDetails.Message
}
