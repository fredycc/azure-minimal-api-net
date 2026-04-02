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
    - Run from repo root where Dockerfile and infra/ exist

.NOTES
    Author:    Fredy Caballero
    Project:   azure-minimal-api-net
    Stack:     .NET 10 / C# 14 / Pulumi C# / Azure Container Apps
    Created:   2026-04-02
    LastEdit:  2026-04-02

    Azure Resources:
        rg-doctors-api-dev          Resource Group (westus2)
        ├── acrdoctorsapidev        Container Registry (Basic)
        ├── law-doctors-api-dev     Log Analytics Workspace
        ├── sql-doctors-api-dev     SQL Server
        │   └── sqldb-doctors-dev   SQL Database (Serverless, auto-pause 60min)
        ├── kv-doctors-api-dev      Key Vault
        ├── cae-doctors-api-dev     Container App Environment
        └── ca-doctors-api-dev      Container App (your API)

    Estimated cost: ~$10/mes idle, ~$15-20/mes with traffic
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
$AcrName  = "acrdoctorsapidev"                     # Azure Container Registry
$RgName   = "rg-doctors-api-$Env"                  # Resource Group
$CaName   = "ca-doctors-api-$Env"                  # Container App
$ImageName = "$AcrName.azurecr.io/doctors-api:$Tag" # Imagen completa con tag

# ──────────────────────────────────────────────────────────────
# STEP 1: INFRAESTRUCTURA (Pulumi)
# ──────────────────────────────────────────────────────────────
# Asegura que todos los recursos Azure existan y estén actualizados.
# Si ya existen y no cambiaron, es un no-op (~2 segundos).
# Si cambiaste infra/Program.cs, aplica los cambios.
#
# Recursos creados por Pulumi:
#   - Resource Group, ACR, Log Analytics, SQL Server/DB, Key Vault,
#     Container App Environment, Container App, Firewall Rule
#
# Te pide la passphrase de Pulumi para desencriptar los secrets.

Write-Host "=== Step 1: Ensure infrastructure exists ===" -ForegroundColor Cyan
pulumi up --cwd infra --yes

# ──────────────────────────────────────────────────────────────
# STEP 2: AUTENTICACIÓN ACR
# ──────────────────────────────────────────────────────────────
# Login a Azure Container Registry para poder hacer push.
# Requiere Azure CLI autenticado (az login previo).

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
# El Container App tiene permiso para pull desde este ACR
# (configurado en infra/Program.cs con registry credentials).
#
# Si cambió poco código, Docker hace push incremental (solo layers nuevos).

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
#   - ConnectionStrings__DefaultConnection (apunta a Azure SQL Serverless)
#   - Auto-migration: la API ejecuta db.Database.MigrateAsync() al startup
#     para aplicar migrations pendientes automáticamente.

Write-Host "`n=== Step 5: Update Container App with new image ===" -ForegroundColor Cyan
az containerapp update --name $CaName --resource-group $RgName --image $ImageName

# ──────────────────────────────────────────────────────────────
# RESULTADO
# ──────────────────────────────────────────────────────────────
# Muestra las URLs finales de la API desplegada.

Write-Host "`n=== Deploy complete! ===" -ForegroundColor Green
$url = az containerapp show --name $CaName --resource-group $RgName --query "properties.configuration.ingress.fqdn" -o tsv
Write-Host "API URL: https://$url"
Write-Host "Swagger:  https://$url/swagger"
Write-Host "ReDoc:    https://$url/redoc.html"
