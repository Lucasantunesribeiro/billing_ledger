Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Iniciando Setup Local do Billing Ledger" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

# Verificar conflito de porta 5433 (Postgres do Docker usa 5433 no host)
Write-Host "0. Verificando disponibilidade de portas..." -ForegroundColor Yellow
$port5433 = netstat -ano | Select-String ":5433 "
if ($port5433) {
    Write-Host "   AVISO: Porta 5433 já está em uso. Verifique se o container já está rodando:" -ForegroundColor Red
    Write-Host "   docker compose ps" -ForegroundColor Yellow
    Write-Host "   Se necessário, pare o processo e rode setup.ps1 novamente." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "1. Subindo infraestrutura Docker..." -ForegroundColor Yellow
docker compose up -d

if ($LASTEXITCODE -ne 0) {
    Write-Host "   ERRO ao subir containers. Verifique 'docker compose ps' e os logs acima." -ForegroundColor Red
    exit 1
}

Write-Host "   Aguardando alguns segundos para os containers inicializarem..."
Start-Sleep -Seconds 10
docker compose ps

Write-Host ""
Write-Host "2. Instalando EF Core Tools (se não instalado)..." -ForegroundColor Yellow
dotnet tool install --global dotnet-ef 2>$null

Write-Host ""
Write-Host "3. Aplicando banco de dados e Migrations..." -ForegroundColor Yellow
Write-Host "   Restaurando pacotes Nuget..."
dotnet restore

Write-Host "   Billing..."
dotnet ef database update --project src/BillingLedger.Billing.Api
Write-Host "   Payments..."
dotnet ef database update --project src/BillingLedger.Payments.Worker
Write-Host "   Ledger..."
dotnet ef database update --project src/BillingLedger.Ledger.Worker

Write-Host ""
Write-Host "4. Gerando Tokens de Acesso..." -ForegroundColor Yellow
Write-Host "   Instalando dotnet-script (se não instalado)..."
dotnet tool install -g dotnet-script 2>$null

$financeToken = dotnet script tools/gen-token.csx dev-user-finance Finance
$adminToken = dotnet script tools/gen-token.csx dev-user-admin Admin
$readOnlyToken = dotnet script tools/gen-token.csx dev-user-readonly ReadOnly

Write-Host "================ TOKENS =====================" -ForegroundColor Green
Write-Host "Copie um destes tokens para usar no teste da API!" -ForegroundColor White
Write-Host "---------------------------------------------"
Write-Host "Role: FINANCE (Pode criar, emitir e cancelar)" -ForegroundColor Magenta
Write-Host $financeToken
Write-Host "---------------------------------------------"
Write-Host "Role: ADMIN (Pode tudo)" -ForegroundColor Magenta
Write-Host $adminToken
Write-Host "---------------------------------------------"
Write-Host "Role: READONLY (Apenas GET)" -ForegroundColor Magenta
Write-Host $readOnlyToken
Write-Host "=============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Tudo pronto! Para iniciar a API e testar execute:" -ForegroundColor Cyan
Write-Host "dotnet run --project src/BillingLedger.Billing.Api"
Write-Host "Depois abra o arquivo tools/api-requests.http para fazer as chamadas HTTP (usando a extensão REST Client)."
