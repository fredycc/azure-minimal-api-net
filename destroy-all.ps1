#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Destruye infraestructura y limpia recursos huérfanos

.DESCRIPTION
    Este script ejecuta pulumi destroy y luego limpia recursos que quedan
    "soft-deleted" en Azure (como Key Vault). 
    
    Usage: .\destroy-all.ps1 [-Env dev|prod]

.PARAMETER Env
    Ambiente a destruir (default: dev)

.EXAMPLE
    .\destroy-all.ps1 -Env dev

#>

param(
    [string]$Env = "dev"
)

$ErrorActionPreference = "Stop"

$location = switch ($Env) {
    "dev"   { "westus2" }
    "prod"  { "eastus" }
    default { "westus2" }
}

$vaultName = "kv-doctors-api-$Env"

Write-Host "=== Step 1: pulumi destroy ===" -ForegroundColor Cyan
pulumi destroy --cwd infra --yes

Write-Host "`n=== Step 2: Cleanup soft-deleted resources ===" -ForegroundColor Cyan

# Limpiar Key Vaults soft-deleted
$deletedVault = az keyvault show-deleted --name $vaultName --query "name" -o tsv 2>$null
if ($deletedVault) {
    Write-Host "  Purging soft-deleted Key Vault: $deletedVault" -ForegroundColor Yellow
    az keyvault purge --name $deletedVault --location $location
    
    # Esperar a que el purge se complete (Azure puede tardar varios segundos)
    Write-Host "  Waiting for purge to complete..." -ForegroundColor Gray
    $maxRetries = 10
    for ($i = 0; $i -lt $maxRetries; $i++) {
        $stillExists = az keyvault show-deleted --name $vaultName --query "name" -o tsv 2>$null
        if (-not $stillExists) {
            Write-Host "  Key Vault purged successfully" -ForegroundColor Green
            break
        }
        Write-Host "  Still purging... ($($i+1)/$maxRetries)" -ForegroundColor Gray
        Start-Sleep -Seconds 3
    }
}
else {
    Write-Host "  No soft-deleted Key Vault found" -ForegroundColor Gray
}

Write-Host "`n=== Cleanup complete ===" -ForegroundColor Green
Write-Host "`nVerifying:" -ForegroundColor Cyan
az keyvault list-deleted --query "[].name" -o tsv