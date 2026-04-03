<#
.SYNOPSIS
    Deploy the Doctors API to Azure (Container Apps + ACR + SQL Serverless)

.DESCRIPTION
    Full deployment pipeline: provisions/updates Azure infrastructure via Pulumi,
    builds the Docker image, pushes it to Azure Container Registry, and updates
    the Container App to run the new image.

    Architecture:
        .NET 10 Minimal API → Docker → ACR → Container App → Azure SQL Serverless

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

    Azure Resources:
        rg-doctors-api-dev          Resource Group (westus2)
        ├── acrdoctorsapidev        Container Registry (Basic, admin enabled)
        ├── law-doctors-api-dev     Log Analytics Workspace (PerGB2018)
        ├── sql-doctors-api-dev     SQL Server
        │   ├── sqldb-doctors-dev   SQL Database (Serverless, auto-pause 60min)
        │   ├── fw-allow-azure      Firewall: AllowAzureServices (0.0.0.0)
        │   └── fw-allow-ca         Firewall: Container App outbound IP
        ├── kv-doctors-api-dev      Key Vault (RBAC mode)
        │   └── sql-connection-string (secret)
        ├── cae-doctors-api-dev     Container App Environment
        ├── ca-doctors-api-dev      Container App (your API)
        │   ├── SystemAssigned Managed Identity
        │   ├── AcrPull role → ACR
        │   └── KeyVaultSecretsUser role → KV
        ├── diag-sql-dev            DiagnosticSettings → Log Analytics
        └── diag-kv-dev             DiagnosticSettings → Log Analytics

    Estimated cost: ~$10/mes idle, ~$15-20/mes with traffic

    Security features:
        - Managed Identity on Container App (no hardcoded passwords)
        - RBAC roles: AcrPull (images), KeyVaultSecretsUser (secrets)
        - Key Vault with RBAC (no legacy AccessPolicies)
        - SQL connection string as Container App secret (not plaintext env var)
        - DiagnosticSettings for SQL and Key Vault audit logs
        - Resource tags for cost tracking

    Known limitations (dev):
        - ACR admin still enabled (TODO: disable after first deploy)
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

$ErrorActionPreference = "Stop"   # Detener en cualquier error

# Nombres de recursos Azure (convención: {service}-doctors-api-{env})
$AcrName   = "acrdoctorsapidev"                     # Azure Container Registry
$RgName    = "rg-doctors-api-$Env"                  # Resource Group
$CaName    = "ca-doctors-api-$Env"                  # Container App
$ImageName = "$AcrName.azurecr.io/doctors-api:$Tag" # Imagen completa con tag

# ──────────────────────────────────────────────────────────────
# PRE-CHECKS: Verificar prerequisitos antes de empezar
# ──────────────────────────────────────────────────────────────

Write-Host "=== Pre-checks ===" -ForegroundColor Cyan

# Verificar Docker Desktop
try { docker info *>$null } catch {
    Write-Error "Docker Desktop no está corriendo. Inicialo y volvé a intentar."
    exit 1
}

# Verificar Azure CLI autenticado
$azAccount = az account show --query "id" -o tsv 2>$null
if (-not $azAccount) {
    Write-Error "Azure CLI no autenticado. Ejecutá: az login"
    exit 1
}

# Verificar Pulumi CLI
try { pulumi version *>$null } catch {
    Write-Error "Pulumi CLI no encontrado. Instalalo: https://www.pulumi.com/docs/install/"
    exit 1
}

Write-Host "  Docker: OK" -ForegroundColor Green
Write-Host "  Azure CLI: OK (subscription: $azAccount)" -ForegroundColor Green
Write-Host "  Pulumi: OK" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# STEP 1: INFRAESTRUCTURA (Pulumi)
# ──────────────────────────────────────────────────────────────
# Asegura que todos los recursos Azure existan y estén actualizados.
# Si ya existen y no cambiaron, es un no-op (~2 segundos).
# Si cambiaste infra/Program.cs, aplica los cambios.
#
# Recursos creados/actualizados por Pulumi:
#   Resource Group, ACR, Log Analytics, SQL Server/DB, Firewall Rules,
#   Key Vault, KV Secret, Container App Environment, Container App,
#   RBAC Role Assignments, DiagnosticSettings
#
# Te pide la passphrase de Pulumi para desencriptar los secrets.
# Si querés evitarlo: $env:PULUMI_CONFIG_PASSPHRASE = "tu-passphrase"

Write-Host "`n=== Step 1: Ensure infrastructure exists ===" -ForegroundColor Cyan
pulumi up --cwd infra --yes

# ──────────────────────────────────────────────────────────────
# STEP 2: AUTENTICACIÓN ACR
# ──────────────────────────────────────────────────────────────
# Login a Azure Container Registry para poder hacer push.
# Requiere Azure CLI autenticado (verificado en pre-checks).

Write-Host "`n=== Step 2: Login to ACR ===" -ForegroundColor Cyan
az acr login --name $AcrName

# ──────────────────────────────────────────────────────────────
# STEP 3: BUILD DOCKER
# ──────────────────────────────────────────────────────────────
# Construye la imagen multi-stage:
#   - Build stage: dotnet restore + publish en SDK 10.0
#   - Runtime stage: aspnet 10.0 (más liviana, ~200MB vs ~800MB SDK)
#
# El .dockerignore excluye bin/, obj/, infra/ para un build limpio.
# IMPORTANTE: **/bin/ y **/obj/ (con glob) para excluir recursivamente.
#             Sin glob, los obj/ de Windows (con rutas C:\...) se copian
#             al contenedor Linux y el build falla.
#
# Si tarda mucho, es la descarga del SDK base (~191MB) la primera vez.

Write-Host "`n=== Step 3: Build Docker image ===" -ForegroundColor Cyan
docker build -t $ImageName .

# ──────────────────────────────────────────────────────────────
# STEP 4: PUSH A ACR
# ──────────────────────────────────────────────────────────────
# Sube la imagen a Azure Container Registry.
# Si cambió poco código, Docker hace push incremental (solo layers nuevos).
#
# NOTA: El primer push crea el repositorio "doctors-api" en el ACR.

Write-Host "`n=== Step 4: Push to ACR ===" -ForegroundColor Cyan
docker push $ImageName

# ──────────────────────────────────────────────────────────────
# STEP 5: ACTUALIZAR CONTAINER APP
# ──────────────────────────────────────────────────────────────
# CRÍTICO: Sin este paso, el Container App sigue corriendo la imagen vieja.
#
# Este comando le dice al Container App que hay una nueva imagen disponible
# y fuerza un pull + restart del contenedor.
#
# El Container App tiene configurado:
#   - ASPNETCORE_ENVIRONMENT=Development (activa Swagger/ReDoc)
#   - ConnectionStrings__DefaultConnection (secret ref → SQL connection string)
#   - Auto-migration: la API ejecuta db.Database.MigrateAsync() al startup
#     para aplicar migrations pendientes automáticamente.
#
# NOTA: Si la DB está pausada (serverless), el primer request tarda ~30s
#       mientras la DB "despierta".

Write-Host "`n=== Step 5: Update Container App with new image ===" -ForegroundColor Cyan
az containerapp update --name $CaName --resource-group $RgName --image $ImageName

# ──────────────────────────────────────────────────────────────
# STEP 6: VERIFICAR DEPLOY
# ──────────────────────────────────────────────────────────────
# Espera a que la nueva revisión esté healthy y muestra las URLs.

Write-Host "`n=== Step 6: Verify deployment ===" -ForegroundColor Cyan

# Esperar a que la nueva revisión esté activa (máx 60 segundos)
$maxRetries = 12
for ($i = 0; $i -lt $maxRetries; $i++) {
    $revision = az containerapp revision list --name $CaName --resource-group $RgName --query "[?active==\`true\`].{name:name, health:healthState}" -o json | ConvertFrom-Json
    $healthy = $revision | Where-Object { $_.health -eq "Healthy" }
    if ($healthy) {
        Write-Host "  Revision healthy: $($healthy.name)" -ForegroundColor Green
        break
    }
    Write-Host "  Waiting for revision to become healthy... ($($i+1)/$maxRetries)" -ForegroundColor Yellow
    Start-Sleep -Seconds 5
}

# ──────────────────────────────────────────────────────────────
# RESULTADO
# ──────────────────────────────────────────────────────────────
# Muestra las URLs finales de la API desplegada.

Write-Host "`n=== Deploy complete! ===" -ForegroundColor Green
$url = az containerapp show --name $CaName --resource-group $RgName --query "properties.configuration.ingress.fqdn" -o tsv
Write-Host "API URL:  https://$url"
Write-Host "Swagger:  https://$url/swagger"
Write-Host "ReDoc:    https://$url/redoc.html"

# ──────────────────────────────────────────────────────────────
# TROUBLESHOOTING (si algo falla)
# ──────────────────────────────────────────────────────────────
#
# La API no responde:
#   az containerapp logs show --name $CaName --resource-group $RgName --type console --tail 30
#
# La revisión está Unhealthy:
#   az containerapp revision list --name $CaName --resource-group $RgName -o table
#   az containerapp revision restart --name $CaName --resource-group $RgName --revision <revision-name>
#
# Error SQL 40615 (Cannot open server):
#   → El firewall de SQL no tiene la IP del Container App
#   → Verificar: az sql server firewall-rule list -s sql-doctors-api-$Env -g $RgName -o table
#   → La IP outbound del Container App:
#     az containerapp show --name $CaName --resource-group $RgName --query "properties.outboundIpAddresses" -o tsv
#
# Error en Pulumi (resource not found):
#   → El recurso fue borrado de Azure pero sigue en el state de Pulumi
#   → Sacar del state: pulumi state delete "<urn>" --cwd infra
#   → Ver URNs: pulumi stack export --cwd infra | Select-String "urn"
#
# Cambiar imageTag sin tocar infra:
#   pulumi config set imageTag "v1.2.3" --cwd infra
#   pulumi up --cwd infra --yes
