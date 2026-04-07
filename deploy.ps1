<#
.SYNOPSIS
    Deploy the Doctors API to Azure (Container Apps + ACR + SQL Serverless)

.DESCRIPTION
    Full deployment pipeline: provisions/updates Azure infrastructure via Pulumi,
    builds the Docker image, pushes it to Azure Container Registry, and creates
    the Container App to run the new image.

    Architecture:
        .NET 10 Minimal API → Docker → ACR → Container App → Azure SQL Serverless

    Flow:
        1. pulumi up → creates ALL infra (RG, ACR, SQL, KV, CAE, Container App, etc.)
        2. docker build + push → pushes image to ACR
        3. az containerapp update → updates image on existing Container App
        4. Verify API health

.PARAMETER Tag
    Docker image tag. Default: "latest"
    Use semantic versioning for releases: .\deploy.ps1 -Tag "1.0.0"

.PARAMETER Env
    Target environment. Default: "dev"
    Affects resource naming: rg-doctors-api-{env}, ca-doctors-api-{env}

.EXAMPLE
    .\deploy.ps1
    Deploy to dev with tag "latest"

.EXAMPLE
    .\deploy.ps1 -Env dev -Tag "1.2.0"
    Deploy version 1.2.0 to dev environment

.EXAMPLE
    .\deploy.ps1 -Tag "hotfix-login"
    Deploy with custom tag for quick iteration

.PREREQUISITES
    - Docker Desktop running
    - Azure CLI authenticated (az account show)
    - Pulumi CLI installed (pulumi version)
    - Pulumi stack selected (pulumi stack select dev --cwd infra)
    - Run from repo root where Dockerfile and infra/ exist

.NOTES
    Author:    Fredy Caballero
    Project:   azure-minimal-api-net
    Stack:     .NET 10 / C# 14 / Pulumi C# / Azure Container Apps
    Created:   2026-04-02
    LastEdit:  2026-04-03

    Azure Resources created by Pulumi:
        rg-doctors-api-dev          Resource Group (westus2)
        ├── acrdoctorsapidev        Container Registry (Basic, no admin)
        ├── law-doctors-api-dev     Log Analytics Workspace (PerGB2018)
        ├── sql-doctors-api-dev     SQL Server
        │   ├── sqldb-doctors-dev   SQL Database (Serverless, auto-pause 60min)
        │   ├── fw-allow-azure      Firewall: AllowAzureServices (0.0.0.0)
        │   └── AllowContainerApp   Firewall: Container App outbound IP
        ├── kv-doctors-api-dev      Key Vault (RBAC mode)
        │   ├── sql-conn-dev        Connection string secret
        │   └── jwt-signing-key-dev JWT signing key secret
        ├── cae-doctors-api-dev     Container App Environment
        ├── ca-doctors-api-dev      Container App (placeholder image, MinReplicas=0)
        │   ├── SystemAssigned Managed Identity
        │   ├── AcrPull role → ACR
        │   └── KeyVaultSecretsUser role → KV
        ├── diag-sql-dev            DiagnosticSettings → Log Analytics
        └── diag-kv-dev             DiagnosticSettings → Log Analytics

    Azure Resources updated by deploy.ps1:
        └── ca-doctors-api-dev      Container App (image update only)

    Estimated cost: ~$10/mes idle, ~$15-20/mes with traffic

    Security features:
        - Managed Identity on Container App (no hardcoded passwords)
        - RBAC roles: AcrPull (images), KeyVaultSecretsUser (secrets)
        - Key Vault with RBAC (no legacy AccessPolicies)
        - SQL connection string as Container App secret (not plaintext env var)
        - DiagnosticSettings for SQL and Key Vault audit logs
        - ACR admin disabled (Managed Identity only)
        - Resource tags for cost tracking

    Known limitations (dev):
        - SQL firewall 0.0.0.0 (AllowAzureServices) kept for dev
        - No VNet integration (would cost $30-50/mes extra)
        - ASPNETCORE_ENVIRONMENT=Development (Swagger enabled)
#>

param(
    [string]$Tag = "latest",
    [string]$Env = "dev"
)

# ──────────────────────────────────────────────────────────────
# CONFIGURACIÓN
# ──────────────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"

$AcrName   = "acrdoctorsapi$Env"
$RgName    = "rg-doctors-$Env"
$CaName    = "ca-doctors-api-$Env"
$CaeName   = "cae-doctors-api-$Env"
$KvName    = "kv-doctors-api-$Env"
$ImageName = "$AcrName.azurecr.io/doctors-api:$Tag"

# ──────────────────────────────────────────────────────────────
# PRE-CHECKS
# ──────────────────────────────────────────────────────────────

Write-Host "=== Pre-checks ===" -ForegroundColor Cyan

try { docker info *>$null } catch {
    Write-Error "Docker Desktop no está corriendo. Inicialo y volvé a intentar."
    exit 1
}

$azAccount = az account show --query "id" -o tsv 2>$null
if (-not $azAccount) {
    Write-Error "Azure CLI no autenticado. Ejecutá: az login"
    exit 1
}

try { pulumi version *>$null } catch {
    Write-Error "Pulumi CLI no encontrado. Instalalo: https://www.pulumi.com/docs/install/"
    exit 1
}

Write-Host "  Docker: OK" -ForegroundColor Green
Write-Host "  Azure CLI: OK (subscription: $azAccount)" -ForegroundColor Green
Write-Host "  Pulumi: OK" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# PULUMI PASSPHRASE
# ──────────────────────────────────────────────────────────────

$passphraseTempFile = $null
if (-not $env:PULUMI_CONFIG_PASSPHRASE -and -not $env:PULUMI_CONFIG_PASSPHRASE_FILE) {
    Write-Host "`n=== Pulumi Passphrase ===" -ForegroundColor Cyan
    $securePassphrase = Read-Host "Enter your Pulumi passphrase" -AsSecureString
    $plainPassphrase = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassphrase)
    )
    $securePassphrase = $null

    # Write passphrase to temp file instead of env var (prevents leak to child processes)
    $passphraseTempFile = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($passphraseTempFile, $plainPassphrase)
    $plainPassphrase = $null
    $env:PULUMI_CONFIG_PASSPHRASE_FILE = $passphraseTempFile
}

# ──────────────────────────────────────────────────────────────
# PASO 1: INFRAESTRUCTURA BASE (Pulumi)
# ──────────────────────────────────────────────────────────────
# Crea TODA la infra: Resource Group, ACR, SQL Server/DB, Key Vault,
# Log Analytics, Container App Environment, Container App (placeholder),
# Diagnostic Settings, KV Secrets, y roles RBAC.
#
# El Container App se crea con una imagen placeholder pública y
# MinReplicas=0 para evitar errores en el primer deploy.

Write-Host "`n=== Step 1: Provision infrastructure ===" -ForegroundColor Cyan

$oldPref = $ErrorActionPreference
$ErrorActionPreference = "Continue"

pulumi up --cwd infra --yes
$exitCode = $LASTEXITCODE

$ErrorActionPreference = $oldPref

# Cleanup passphrase temp file (only if we created one)
if ($passphraseTempFile) {
    Remove-Item $passphraseTempFile -Force -ErrorAction SilentlyContinue
    $env:PULUMI_CONFIG_PASSPHRASE_FILE = $null
}

if ($exitCode -ne 0) {
    Write-Error "Failed to provision infrastructure. Exit code: $exitCode"
    exit 1
}

Write-Host "  Infrastructure provisioned" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# PASO 2: LOGIN A ACR
# ──────────────────────────────────────────────────────────────
# Autentica Docker contra Azure Container Registry para poder hacer push.

Write-Host "`n=== Step 2: Login to ACR ===" -ForegroundColor Cyan
az acr login --name $AcrName

# ──────────────────────────────────────────────────────────────
# PASO 3: BUILD DOCKER
# ──────────────────────────────────────────────────────────────
# Construye la imagen multi-stage:
#   - Build stage: dotnet restore + publish en SDK 10.0
#   - Runtime stage: aspnet 10.0 (más liviana, ~200MB)

Write-Host "`n=== Step 3: Build Docker image ===" -ForegroundColor Cyan
docker build -t $ImageName .
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed. Exit code: $LASTEXITCODE"
    exit 1
}
Write-Host "  Image built: $ImageName" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# PASO 4: PUSH A ACR
# ──────────────────────────────────────────────────────────────
# Sube la imagen a Azure Container Registry.

Write-Host "`n=== Step 4: Push to ACR ===" -ForegroundColor Cyan
docker push $ImageName
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker push failed. Exit code: $LASTEXITCODE"
    exit 1
}
Write-Host "  Image pushed to ACR" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# PASO 5: CONFIGURAR REGISTRY Y ACTUALIZAR IMAGEN DEL CONTAINER APP
# ──────────────────────────────────────────────────────────────
# El Container App ya fue creado por Pulumi con una imagen placeholder.
# Primero configuramos el ACR con la identidad del sistema (necesario para pull).
# Luego actualizamos la imagen.

Write-Host "`n=== Step 5: Configure registry and update Container App image ===" -ForegroundColor Cyan

# Configurar el registry con identidad del sistema
Write-Host "  Configuring ACR registry with system identity..." -ForegroundColor Yellow
az containerapp registry set `
    --name $CaName `
    --resource-group $RgName `
    --server "$AcrName.azurecr.io" `
    --identity system 2>&1 | Out-Null

# Actualizar la imagen
Write-Host "  Updating Container App image..." -ForegroundColor Yellow
$updateResult = az containerapp update `
    --name $CaName `
    --resource-group $RgName `
    --image $ImageName 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "  First update attempt failed, retrying..." -ForegroundColor Yellow
    Start-Sleep -Seconds 2
    az containerapp update --name $CaName --resource-group $RgName --image $ImageName 2>&1 | Out-Null
}

# Obtener la URL del Container App
$caFqdn = az containerapp show --name $CaName --resource-group $RgName --query "properties.configuration.ingress.fqdn" -o tsv 2>$null

if ($LASTEXITCODE -ne 0 -or -not $caFqdn) {
    Write-Error "Failed to update Container App image."
    exit 1
}

Write-Host "  Container App ready: https://$caFqdn" -ForegroundColor Green

Write-Host "  Container App image updated: $ImageName" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# PASO 6: VERIFICAR DEPLOY
# ──────────────────────────────────────────────────────────────
# Espera a que la API responda (máx 120s — SQL serverless puede
# tardar al despertar). Muestra las URLs finales.

Write-Host "`n=== Step 6: Verify deployment ===" -ForegroundColor Cyan

$url = az containerapp show --name $CaName --resource-group $RgName --query "properties.configuration.ingress.fqdn" -o tsv 2>$null

if (-not $url -or $url -eq "null") {
    Write-Warning "Container App no tiene ingress configurado."
    Write-Host "  Verificá el estado: az containerapp show --name $CaName --resource-group $RgName --query 'properties.provisioningState' -o tsv"
}
else {
    Write-Host "  Container App URL: https://$url" -ForegroundColor Cyan
    Write-Host "  Waiting for API to respond... (máx 120s — SQL serverless puede tardar al despertar)" -ForegroundColor Yellow

    $maxRetries = 24
    $apiResponded = $false

    for ($i = 0; $i -lt $maxRetries; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "https://$url/api/doctors" -Method GET -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
            Write-Host "  API is responding (HTTP $($response.StatusCode))" -ForegroundColor Green
            $apiResponded = $true
            break
        } catch {
            Write-Host "  Waiting for API... ($($i+1)/$maxRetries)" -ForegroundColor Yellow
            Start-Sleep -Seconds 5
        }
    }

    if (-not $apiResponded) {
        Write-Warning "La API no respondió después de 120 segundos."
        Write-Host "`n  Para ver logs del container:" -ForegroundColor Yellow
        Write-Host "    az containerapp logs show --name $CaName --resource-group $RgName --type console --tail 30" -ForegroundColor Gray
    }
}

# ──────────────────────────────────────────────────────────────
# RESULTADO
# ──────────────────────────────────────────────────────────────
# Muestra las URLs finales de la API desplegada.
