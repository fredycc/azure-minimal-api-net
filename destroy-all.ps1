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
    [ValidateSet('dev','qa','prod')]
    [string]$Env,
    [switch]$DestroyShared
)

if (-not $Env) { Write-Error 'Debe especificar el ambiente explícitamente: .\destroy-all.ps1 -Env dev|qa|prod'; exit 1 }

$ErrorActionPreference = "Stop"

# ──────────────────────────────────────────────────────────────
# PULUMI PASSPHRASE (same pattern as deploy.ps1)
# ──────────────────────────────────────────────────────────────

$passphraseTempFile = $null
if (-not $env:PULUMI_CONFIG_PASSPHRASE -and -not $env:PULUMI_CONFIG_PASSPHRASE_FILE) {
    Write-Host "=== Pulumi Passphrase ===" -ForegroundColor Cyan
    $securePassphrase = Read-Host "Enter your Pulumi passphrase" -AsSecureString
    $plainPassphrase = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassphrase)
    )
    $securePassphrase = $null

    $passphraseTempFile = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($passphraseTempFile, $plainPassphrase)
    $plainPassphrase = $null
    $env:PULUMI_CONFIG_PASSPHRASE_FILE = $passphraseTempFile
}

# ──────────────────────────────────────────────────────────────

$location = switch ($Env) {
    "dev"   { "westus2" }
    "qa"    { "westus2" }
    "prod"  { "eastus" }
    default { "westus2" }
}

$vaultName = "kv-doctors-api-$Env"

Write-Host "=== Multi-Stage Destroy: Environment '$Env' (Shared remains protected) ===" -ForegroundColor Cyan

if ($DestroyShared) {
    Write-Host "`n⚠️  WARNING: You are about to destroy SHARED infrastructure (ACR + LAW)" -ForegroundColor Red
    Write-Host "   This affects ALL environments (dev, qa, prod)!" -ForegroundColor Red
    $confirm = Read-Host "Type 'DESTROY-SHARED' to confirm"
    if ($confirm -ne "DESTROY-SHARED") {
        Write-Host "Aborted." -ForegroundColor Yellow
        exit 0
    }
    Write-Host "  Destroying shared infrastructure..." -ForegroundColor Red
    pulumi destroy --cwd infra-shared --stack main --yes
}

Write-Host "`n=== Step 1: pulumi destroy environment stack ($Env) ===" -ForegroundColor Cyan
pulumi destroy --cwd infra --stack $Env --yes
$destroyExitCode = $LASTEXITCODE

# Cleanup passphrase temp file
if ($passphraseTempFile) {
    Remove-Item $passphraseTempFile -Force -ErrorAction SilentlyContinue
    $env:PULUMI_CONFIG_PASSPHRASE_FILE = $null
}

if ($destroyExitCode -ne 0) {
    Write-Warning "pulumi destroy exited with code $destroyExitCode. Continuing cleanup..."
}

Write-Host "`n=== Step 2: Cleanup soft-deleted resources (Key Vault) ===" -ForegroundColor Cyan

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

Write-Host "`n⚠️  NOTE: Shared infrastructure (rg-main-shared, acrmain, law-main) was NOT destroyed." -ForegroundColor Green
Write-Host "   To destroy shared resources use: .\destroy-all.ps1 -DestroyShared" -ForegroundColor Yellow
Write-Host "`nOther Azure resources (SQL Server, etc.) may be soft-deleted. Check with:" -ForegroundColor Gray
Write-Host '   az keyvault list-deleted --query "[].{name:name, location:properties.location}" -o table' -ForegroundColor Gray