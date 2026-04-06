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
        1. pulumi up → creates base infra (RG, ACR, SQL, KV, CAE, etc.)
        2. docker build + push → pushes image to ACR
        3. az containerapp create → creates the Container App with the image
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
        ├── acrdoctorsapidev        Container Registry (Basic, admin enabled)
        ├── law-doctors-api-dev     Log Analytics Workspace (PerGB2018)
        ├── sql-doctors-api-dev     SQL Server
        │   ├── sqldb-doctors-dev   SQL Database (Serverless, auto-pause 60min)
        │   └── fw-allow-azure      Firewall: AllowAzureServices (0.0.0.0)
        ├── kv-doctors-api-dev      Key Vault (RBAC mode)
        │   └── sql-connection-string (secret)
        ├── cae-doctors-api-dev     Container App Environment
        ├── diag-sql-dev            DiagnosticSettings → Log Analytics
        └── diag-kv-dev             DiagnosticSettings → Log Analytics

    Azure Resources created by deploy.ps1:
        └── ca-doctors-api-dev      Container App (your API)
            ├── SystemAssigned Managed Identity
            ├── AcrPull role → ACR
            └── KeyVaultSecretsUser role → KV

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

$AcrName   = "acrdoctorsapidev"
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

if (-not $env:PULUMI_CONFIG_PASSPHRASE -and -not $env:PULUMI_CONFIG_PASSPHRASE_FILE) {
    Write-Host "`n=== Pulumi Passphrase ===" -ForegroundColor Cyan
    $securePassphrase = Read-Host "Enter your Pulumi passphrase" -AsSecureString
    $env:PULUMI_CONFIG_PASSPHRASE = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassphrase)
    )
    $securePassphrase = $null
}

# ──────────────────────────────────────────────────────────────
# PASO 1: INFRAESTRUCTURA BASE (Pulumi)
# ──────────────────────────────────────────────────────────────
# Crea: Resource Group, ACR, SQL Server/DB, Key Vault, Log Analytics,
#       Container App Environment, Diagnostic Settings, KV Secret,
#       y rol Key Vault Secrets Officer para el usuario de Pulumi.
#
# NO crea el Container App — ese se crea en el Paso 5 via Azure CLI
# porque necesita la imagen Docker que aún no existe.

Write-Host "`n=== Step 1: Provision infrastructure ===" -ForegroundColor Cyan

$oldPref = $ErrorActionPreference
$ErrorActionPreference = "Continue"

pulumi up --cwd infra --yes
$exitCode = $LASTEXITCODE

$ErrorActionPreference = $oldPref

if ($exitCode -ne 0) {
    Write-Error "Failed to provision infrastructure. Exit code: $exitCode"
    exit 1
}

Write-Host "  Infrastructure provisioned" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# PASO 2: LOGIN A ACR
# ──────────────────────────────────────────────────────────────
# Autentica Docker contra Azure Container Registry para poder hacer push.

# ──────────────────────────────────────────────────────────────
# PASO 3: BUILD DOCKER
# ──────────────────────────────────────────────────────────────
# Construye la imagen multi-stage:
#   - Build stage: dotnet restore + publish en SDK 10.0
#   - Runtime stage: aspnet 10.0 (más liviana, ~200MB)

# ──────────────────────────────────────────────────────────────
# PASO 4: PUSH A ACR
# ──────────────────────────────────────────────────────────────
# Sube la imagen a Azure Container Registry.

# ──────────────────────────────────────────────────────────────
# PASO 5: CREAR CONTAINER APP (Azure CLI)
# ──────────────────────────────────────────────────────────────
# El Container App se crea con Azure CLI (no con Pulumi) porque:
#   1. La imagen ya existe en el ACR (build+push completado)
#   2. Evita condiciones de carrera con el rol AcrPull
#   3. No requiere workarounds de MinReplicas=0 en Pulumi
#
# Se obtiene el connection string del Key Vault y se pasa como secret.
# Si el Container App ya existe, solo se actualiza la imagen.

# ──────────────────────────────────────────────────────────────
# PASO 6: FIREWALL SQL PARA CONTAINER APP
# ──────────────────────────────────────────────────────────────
# El alias "AllowAzureServices" (0.0.0.0) NO cubre Container Apps.
# Se crea una regla con la IP outbound real del Container App.

# ──────────────────────────────────────────────────────────────
# PASO 7: VERIFICAR DEPLOY
# ──────────────────────────────────────────────────────────────
# Espera a que la API responda (máx 120s — SQL serverless puede
# tardar al despertar). Muestra las URLs finales.

Write-Host "`n=== Step 2: Login to ACR ===" -ForegroundColor Cyan
az acr login --name $AcrName

# ──────────────────────────────────────────────────────────────
# STEP 3: BUILD DOCKER
# ──────────────────────────────────────────────────────────────

Write-Host "`n=== Step 3: Build Docker image ===" -ForegroundColor Cyan
docker build -t $ImageName .

# ──────────────────────────────────────────────────────────────
# STEP 4: PUSH A ACR
# ──────────────────────────────────────────────────────────────

Write-Host "`n=== Step 4: Push to ACR ===" -ForegroundColor Cyan
docker push $ImageName

# ──────────────────────────────────────────────────────────────
# STEP 5: CREAR CONTAINER APP (Azure CLI)
# ──────────────────────────────────────────────────────────────
# El Container App se crea con Azure CLI porque:
#   1. La imagen ya existe en el ACR (build+push completado)
#   2. Evita condiciones de carrera con el rol AcrPull
#   3. No requiere workarounds de MinReplicas=0 en Pulumi
#
# Se usa Managed Identity (SystemAssigned) con roles AcrPull y
# KeyVaultSecretsUser para autenticación sin credenciales.

Write-Host "`n=== Step 5: Create Container App ===" -ForegroundColor Cyan

# Obtener secrets del Key Vault
$sqlConnSecret = az keyvault secret show --vault-name $KvName --name "sql-conn-dev-westus2" --query "value" -o tsv 2>$null
$jwtSigningKey = az keyvault secret show --vault-name $KvName --name "jwt-signing-key-dev" --query "value" -o tsv 2>$null

if (-not $sqlConnSecret) {
    Write-Error "No se pudo obtener el connection string del Key Vault."
    exit 1
}

if (-not $jwtSigningKey) {
    Write-Error "No se pudo obtener el JWT signing key del Key Vault."
    exit 1
}

# Verificar si el Container App ya existe
$caExists = az containerapp show --name $CaName --resource-group $RgName --query "name" -o tsv 2>$null

if ($caExists) {
    Write-Host "  Container App already exists. Updating image..." -ForegroundColor Yellow
    az containerapp update --name $CaName --resource-group $RgName --image $ImageName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to update Container App image."
        exit 1
    }

    # Verificar que tiene Managed Identity (si fue creado sin --system-assigned)
    $caPrincipalId = az containerapp show --name $CaName --resource-group $RgName `
        --query "identity.principalId" -o tsv 2>$null

    if (-not $caPrincipalId -or $caPrincipalId -eq "null") {
        Write-Host "  Assigning SystemAssigned identity to existing Container App..." -ForegroundColor Yellow
        az containerapp identity assign --name $CaName --resource-group $RgName --system-assigned | Out-Null
        $caPrincipalId = az containerapp show --name $CaName --resource-group $RgName `
            --query "identity.principalId" -o tsv 2>$null
    }

    # Verificar que tiene rol AcrPull
    $acrId = az acr show --name $AcrName --query "id" -o tsv 2>$null
    $acrPullRoleId = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
    $existingRole = az role assignment list --assignee $caPrincipalId --role $acrPullRoleId --scope $acrId --query "[0].id" -o tsv 2>$null

    if (-not $existingRole) {
        Write-Host "  Assigning AcrPull role to existing Container App..." -ForegroundColor Yellow
        az role assignment create `
            --assignee-object-id $caPrincipalId `
            --assignee-principal-type ServicePrincipal `
            --role $acrPullRoleId `
            --scope $acrId | Out-Null
        Write-Host "  AcrPull role assigned" -ForegroundColor Green
    }
    else {
        Write-Host "  AcrPull role already assigned" -ForegroundColor Green
    }
}
else {
    Write-Host "  Creating Container App with Managed Identity..." -ForegroundColor Yellow
    
    # Crear el Container App con SystemAssigned Managed Identity
    az containerapp create `
        --name $CaName `
        --resource-group $RgName `
        --environment $CaeName `
        --image $ImageName `
        --target-port 8080 `
        --ingress external `
        --min-replicas 1 `
        --max-replicas 5 `
        --cpu 0.25 `
        --memory "0.5Gi" `
        --registry-server "$AcrName.azurecr.io" `
        --registry-identity system `
        --system-assigned `
        --env-vars ASPNETCORE_ENVIRONMENT=Development "ConnectionStrings__DefaultConnection=secretref:sql-connection-string" "JwtSettings__SigningKey=secretref:jwt-signing-key" `
        --secrets "sql-connection-string=$sqlConnSecret" "jwt-signing-key=$jwtSigningKey" `
        --query "properties.provisioningState" -o tsv

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to create Container App."
        exit 1
    }

    # Asignar rol AcrPull al Managed Identity del Container App
    # Sin esto, el Container App no puede hacer pull de imágenes del ACR
    # (necesario cuando AdminUserEnabled = false)
    Write-Host "  Assigning AcrPull role to Container App..." -ForegroundColor Yellow

    $caPrincipalId = az containerapp show --name $CaName --resource-group $RgName `
        --query "identity.principalId" -o tsv 2>$null

    if ($caPrincipalId) {
        $acrId = az acr show --name $AcrName --query "id" -o tsv 2>$null
        $acrPullRoleId = "7f951dda-4ed3-4680-a7ca-43fe172d538d" # AcrPull (built-in)

        az role assignment create `
            --assignee-object-id $caPrincipalId `
            --assignee-principal-type ServicePrincipal `
            --role $acrPullRoleId `
            --scope $acrId | Out-Null

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "AcrPull role assignment failed. Container App may not be able to pull images."
        }
        else {
            Write-Host "  AcrPull role assigned" -ForegroundColor Green
        }
    }
    else {
        Write-Warning "Could not get Container App principal ID. AcrPull role not assigned."
    }
}

Write-Host "  Container App ready with image: $ImageName" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────
# STEP 6: CONFIGURAR FIREWALL RULE PARA SQL
# ──────────────────────────────────────────────────────────────
# El Container App necesita acceso al SQL Server.
# Creamos una firewall rule con la IP outbound del Container App.

Write-Host "`n=== Step 6: Configure SQL firewall ===" -ForegroundColor Cyan

$caOutboundIp = az containerapp show --name $CaName --resource-group $RgName --query "properties.outboundIpAddresses[0]" -o tsv 2>$null

if ($caOutboundIp) {
    # Verificar si la regla ya existe
    $fwExists = az sql server firewall-rule show `
        --server "sql-doctors-api-$Env" `
        --resource-group $RgName `
        --name "AllowContainerApp" `
        --query "name" -o tsv 2>$null

    if ($fwExists) {
        Write-Host "  Updating firewall rule for IP: $caOutboundIp" -ForegroundColor Yellow
        az sql server firewall-rule update `
            --server "sql-doctors-api-$Env" `
            --resource-group $RgName `
            --name "AllowContainerApp" `
            --start-ip-address $caOutboundIp `
            --end-ip-address $caOutboundIp | Out-Null
    }
    else {
        Write-Host "  Creating firewall rule for IP: $caOutboundIp" -ForegroundColor Yellow
        az sql server firewall-rule create `
            --server "sql-doctors-api-$Env" `
            --resource-group $RgName `
            --name "AllowContainerApp" `
            --start-ip-address $caOutboundIp `
            --end-ip-address $caOutboundIp | Out-Null
    }
    Write-Host "  Firewall rule configured" -ForegroundColor Green
}
else {
    Write-Warning "Could not get Container App outbound IP. SQL access may be blocked."
}

# ──────────────────────────────────────────────────────────────
# STEP 7: VERIFICAR DEPLOY
# ──────────────────────────────────────────────────────────────

Write-Host "`n=== Step 7: Verify deployment ===" -ForegroundColor Cyan

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
