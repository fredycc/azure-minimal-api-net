# Doctors API — Specialist Doctors CRUD

> CRUD API for managing specialist doctors. Built with .NET 10, Clean Architecture, deployed to Azure via Pulumi.

---

## Table of Contents

- [Architecture](#architecture)
- [Technologies](#technologies)
- [Project Structure](#project-structure)
- [Infrastructure](#infrastructure)
  - [Azure Resources](#azure-resources-pulumi)
  - [Security Features](#security-features)
  - [Estimated Cost](#estimated-cost-dev)
  - [Tech Debt](#tech-debt-for-production)
- [Git Setup & Publishing](#git-setup--publishing)
  - [Initialize Git](#1-initialize-git)
  - [Verify ignored files](#2-verify-ignored-files)
  - [First commit](#3-first-commit)
  - [Publish to GitHub](#4-publish-to-github)
  - [Public vs Private — Considerations](#public-vs-private--considerations)
  - [Security — Files that should NEVER be committed](#security--files-that-should-never-be-committed)
  - [Daily workflow](#daily-workflow)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [First-time Setup](#first-time-setup)
  - [Run Locally](#run-locally)
  - [Deploy to Azure](#deploy-to-azure)
  - [Destroy & Recreate Infrastructure](#destroy--recreate-infrastructure)
- [API Endpoints](#api-endpoints)
- [Configuration](#configuration)
- [Troubleshooting](#troubleshooting)
- [Known Issues](#known-issues)

---

## Architecture

### Clean Architecture — 4 Layers

```
                         ┌─────────────────────────────────────┐
                         │           Doctors.Api               │
                         │        (Presentation Layer)         │
                         │                                     │
                         │  Program.cs     ← Composition Root  │
                         │  Endpoints/     ← Route Handlers    │
                         │  Filters/       ← Cross-cutting     │
                         │  Extensions/    ← ProblemDetails    │
                         └────────────────┬────────────────────┘
                                          │ depends on
                         ┌────────────────▼────────────────────┐
                         │       Doctors.Application           │
                         │        (Use Cases Layer)            │
                         │                                     │
                         │  Services/DoctorService.cs          │
                         │  DTOs/ ← Request & Response         │
                         │  Interfaces/ ← Contracts            │
                         │  Mappings/ ← Entity ↔ DTO           │
                         └─────────────────┬───────────┬───────┘
                            depends on ↓   │           │ depends on ↓
                  ┌─────────────────────┐   │           │   ┌──────────────────────┐
                  │   Doctors.Domain    │◄──┘           └──►│Doctors.Infrastructure│
                  │   (Core Layer)      │                   │ (Persistence Layer)  │
                  │                     │                   │                      │
                  │  Entities/Doctor.cs │                   │  Data/DoctorDbContext │
                  │  Exceptions/        │                   │  Repositories/       │
                  └─────────────────────┘                   │  Configurations/     │
                                                            │  Migrations/         │
                                                            └──────────────────────┘
```

### Dependency Rule

```
  Api ──────► Application ──────► Domain ◄────── Infrastructure
                                    ▲
                                    │
                              Zero dependencies
                            (no NuGet, no project refs)
```

**Arrows point INWARD.** Domain never references other layers. Infrastructure implements Application interfaces.

### SOLID Principles Applied

| Principle | Where |
|-----------|-------|
| **S**ingle Responsibility | `DoctorService` orchestrates. `DoctorRepository` persists. `DoctorEndpoints` translates HTTP. Each class ONE job. |
| **O**pen/Closed | Swap InMemory for SQL Server = change only `DependencyInjection.cs`. Business logic untouched. |
| **L**iskov Substitution | `DoctorRepository` can replace `IDoctorRepository` anywhere. |
| **I**nterface Segregation | `IDoctorRepository` has only 5 methods needed for CRUD. |
| **D**ependency Inversion | `DoctorService` depends on `IDoctorRepository` (abstraction), not `DoctorRepository` (concrete). |

---

## Technologies

| Layer | Technology | Version | Purpose |
|-------|-----------|---------|---------|
| **Runtime** | .NET | 10.0 | Application framework |
| **Language** | C# | 14 | Primary constructors, `required`, `field` keyword |
| **API** | ASP.NET Core Minimal API | 10.0 | HTTP endpoints |
| **ORM** | Entity Framework Core | 10.0 | Database access |
| **Database** | Azure SQL Serverless | — | Serverless, auto-pause after 60min idle |
| **Docs** | NSwag.AspNetCore | 14.6.3 | Swagger UI + OpenAPI spec |
| **IaC** | Pulumi Azure Native | 3.x | Infrastructure as Code (C#) |
| **Hosting** | Azure Container Apps | — | Serverless containers |
| **Registry** | Azure Container Registry | — | Docker image storage |
| **Secrets** | Azure Key Vault | — | Connection strings, passwords |
| **Logging** | Log Analytics Workspace | — | Centralized Azure logs |
| **Container** | Docker | — | Multi-stage build |

### C# 14 Features Used

```csharp
// Primary constructors (DI without boilerplate)
public class DoctorService(IDoctorRepository repository) : IDoctorService { }

// Required properties (compiler-enforced initialization)
public required string FirstName { get; set; }

// Field keyword (inline validation in setters)
public required string LicenseNumber
{
    get => field;
    set { if (!Regex.IsMatch(value, @"^[A-Z0-9-]+$")) throw new ...; field = value; }
}

// Collection expressions
return [.. doctors.Select(d => d.ToDto())];
```

---

## Project Structure

```
azure-minimal-api-net/
│
├── src/
│   ├── Doctors.Domain/                    # ← Zero dependencies
│   │   ├── Entities/
│   │   │   └── Doctor.cs                  # Core entity (required, field keyword, soft-delete)
│   │   └── Exceptions/
│   │       ├── DomainException.cs         # Base exception
│   │       ├── NotFoundException.cs       # → HTTP 404
│   │       └── ConflictException.cs       # → HTTP 409
│   │
│   ├── Doctors.Application/              # ← Depends on Domain
│   │   ├── DTOs/
│   │   │   ├── DoctorDto.cs               # Response DTO
│   │   │   ├── CreateDoctorRequest.cs     # POST body
│   │   │   └── UpdateDoctorRequest.cs     # PUT body
│   │   ├── Interfaces/
│   │   │   └── IDoctorRepository.cs       # Persistence contract
│   │   ├── Services/
│   │   │   ├── IDoctorService.cs          # Service contract
│   │   │   └── DoctorService.cs           # CRUD orchestration (primary constructor)
│   │   ├── Mappings/
│   │   │   └── DoctorMappingExtensions.cs # Entity ↔ DTO mapping
│   │   └── DependencyInjection.cs         # AddApplication() extension
│   │
│   ├── Doctors.Infrastructure/           # ← Depends on Application
│   │   ├── Data/
│   │   │   ├── DoctorDbContext.cs         # EF Core context (primary constructor)
│   │   │   ├── DoctorDbContextFactory.cs  # Design-time factory (for migrations)
│   │   │   ├── Configurations/
│   │   │   │   └── DoctorConfiguration.cs # Table mapping, unique index, query filter
│   │   │   └── Migrations/
│   │   │       └── *.cs                   # EF Core migrations
│   │   ├── Repositories/
│   │   │   └── DoctorRepository.cs        # Implements IDoctorRepository
│   │   └── DependencyInjection.cs         # AddInfrastructure() extension
│   │
│   └── Doctors.Api/                      # ← Depends on Application + Infrastructure
│       ├── Program.cs                     # Composition root
│       ├── Endpoints/
│       │   └── DoctorEndpoints.cs         # 5 CRUD route handlers
│       ├── Filters/
│       │   ├── LoggingFilter.cs           # Stopwatch-based request logging
│       │   └── ValidationFilter.cs        # DataAnnotations validation
│       ├── Extensions/
│       │   └── ProblemDetailsExtensions.cs# Global exception → HTTP mapping
│       ├── wwwroot/
│       │   └── redoc.html                 # Static ReDoc documentation page
│       └── Properties/
│           └── launchSettings.json        # Dev environment config
│
├── infra/                                # Pulumi Infrastructure as Code
│   ├── Pulumi.yaml                        # Project definition
│   ├── Pulumi.dev.yaml                    # Stack config (secrets encrypted)
│   ├── infra.csproj                       # Pulumi project
│   └── Program.cs                         # Azure resource definitions (documented in Spanish)
│
├── Dockerfile                             # Multi-stage: SDK 10.0 → ASP.NET 10.0
├── .dockerignore                          # Excludes bin/, obj/, infra/
├── deploy.ps1                             # One-command deploy script (with pre-checks)
├── Directory.Build.props                  # Shared: net10.0, LangVersion 14
└── azure-minimal-api-net.slnx             # Solution file (.NET 10 format)
```

### Files NOT in repo (security)

| File | Why | How to create |
|------|-----|---------------|
| `infra/Pulumi.dev.yaml` | Contains encrypted secrets (SQL password) | See [First-time Setup](#first-time-setup) |
| `src/Doctors.Api/Properties/launchSettings.json` | Local dev settings | Auto-created by IDE, or see setup below |

---

## Infrastructure

### Azure Resources (Pulumi)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  rg-doctors-dev                                  Resource Group (westus2)   │
│                                                                              │
│  ├── acrdoctorsapidev                            Container Registry (Basic)  │
│  │   ├── AdminUserEnabled: true (temporary)                                  │
│  │   └── doctors-api:latest (or imageTag from config)                        │
│  │                                                                          │
│  ├── law-doctors-api-dev                         Log Analytics Workspace     │
│  │   └── SKU: PerGB2018 (pay-per-use)                                       │
│  │                                                                          │
│  ├── sql-doctors-api-dev                         SQL Server                  │
│  │   ├── sqldb-doctors-dev                       SQL Database (Serverless)   │
│  │   │   ├── AutoPauseDelay: 60 min              ← $0 when idle              │
│  │   │   ├── MinCapacity: 0.5 vCores             ← Minimum when active       │
│  │   │   ├── MaxSize: 2 GB                       ← Enough for dev            │
│  │   │   └── diag-sql-dev → Log Analytics         ← Diagnostics (5 categories)│
│  │   └── fw-allow-azure (0.0.0.0–0.0.0.0)       ← AllowAzureServices        │
│  │                                                                          │
│  ├── kv-doctors-api-dev                          Key Vault (RBAC mode)       │
│  │   ├── EnableRbacAuthorization: true                                       │
│  │   ├── sql-connection-string (secret)                                      │
│  │   ├── kvadmin-dev → User (Key Vault Secrets Officer)                      │
│  │   └── diag-kv-dev → Log Analytics             ← Audit logs                │
│  │                                                                          │
│  └── cae-doctors-api-dev                         Container App Environment   │
│                                                                              │
│  └── Tags: environment=dev, project=azure-minimal-api-net, managed-by=pulumi │
│                                                                              │
│  Outputs:                                                                    │
│  ├── acrName: acrdoctorsapidev                                               │
│  └── resourceGroupName: rg-doctors-dev                                      │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Azure Resources (deploy.ps1 — Azure CLI)

El Container App se crea via Azure CLI (no Pulumi) porque necesita la imagen
Docker que aún no existe cuando se corre `pulumi up`.

```
  └── ca-doctors-api-dev                      Container App (creado por deploy.ps1)
      ├── Image: acrdoctorsapidev.azurecr.io/doctors-api:{Tag}
      ├── Port: 8080
      ├── Registry: acrdoctorsapidev.azurecr.io (con credenciales admin)
      ├── Env:
      │   ├── ASPNETCORE_ENVIRONMENT=Development
      │   └── ConnectionStrings__DefaultConnection (secretref → KV secret)
      ├── Scale: 1–5 replicas
      └── fw-allow-ca → SQL firewall rule (CA outbound IP)
```

> **infra/Program.cs** is fully documented with 12 sections explaining architecture, each resource, security, and costs. Read the file to understand each decision.

### Security Features

| Feature | Implementation | Cost |
|---------|---------------|------|
| **Managed Identity** | SystemAssigned on Container App | $0 |
| **ACR Pull via RBAC** | AcrPull role (no admin credentials) | $0 |
| **Key Vault RBAC** | EnableRbacAuthorization = true | $0 |
| **Secret Reference** | Connection string via SecretRef (no plaintext) | $0 |
| **Resource Tags** | environment, project, managed-by | $0 |
| **DiagnosticSettings** | SQL + Key Vault → Log Analytics | ~$2-5/mo |
| **SQL Firewall** | AllowAzureServices + Container App outbound IP | $0 |

### Estimated Cost (Dev)

| Resource | Idle | With Traffic |
|----------|------|-------------|
| Resource Group | $0 | $0 |
| Container App | $0 | $0–15 |
| SQL Serverless (auto-pause) | $0 | $0–3 |
| ACR Basic | $5 | $5 |
| Log Analytics | $2–5 | $2–5 |
| Key Vault | $3 | $3 |
| DiagnosticSettings | $0–2 | $0–2 |
| **Total** | **~$10/mo** | **~$15–20/mo** |

### Tech Debt (for Production)

| Item | Dev | Production |
|------|-----|-----------|
| ACR Admin | Enabled (temporary) | Disable + use AcrPull only |
| SQL Firewall | AllowAzureServices (0.0.0.0) | VNet + Private Endpoint (~$30-50/mo) |
| SQL Auth | Admin password | Azure AD auth |
| ASPNETCORE_ENVIRONMENT | Development | Production |
| ACR Tier | Basic ($5/mo) | Standard ($150/mo, vulnerability scanning) |

---

## Git Setup & Publishing

### 1. Initialize Git

```bash
# Initialize the local repository
git init

# Create main branch (instead of "master")
git checkout -b main
```

### 2. Verify ignored files

The project includes a `.gitignore` configured to exclude:

| File/Folder | Reason |
|-------------|--------|
| `bin/`, `obj/` | .NET build artifacts |
| `.vs/` | Visual Studio config |
| `infra/Pulumi.dev.yaml` | Contains encrypted secrets (SQL password) |
| `appsettings.Development.json` | Local development config |
| `.env` | Local environment variables |

### 3. First commit

```bash
git add .
git commit -m "feat: initial commit — azure minimal api with clean architecture"
```

### 4. Publish to GitHub

#### Option A: Public Repository

```bash
gh repo create azure-minimal-api-net \
  --public \
  --source=. \
  --remote=origin \
  --push \
  --description "CRUD API for specialist doctors — .NET 10, Clean Architecture, Azure Container Apps"
```

#### Option B: Private Repository

```bash
gh repo create azure-minimal-api-net \
  --private \
  --source=. \
  --remote=origin \
  --push \
  --description "CRUD API for specialist doctors — .NET 10, Clean Architecture, Azure Container Apps"
```

### 5. Configure collaborators (private repos only)

```bash
gh api repos/YOUR_USERNAME/azure-minimal-api-net/collaborators/COLLABORATOR \
  --method PUT \
  -f permission=push
```

### Public vs Private — Considerations

| Aspect | Public | Private |
|--------|--------|---------|
| **Visibility** | Anyone can view the code | Only invited collaborators |
| **Secrets** | NEVER upload `Pulumi.dev.yaml`, `.env`, keys | More secure, but still don't upload secrets |
| **Cost** | Free (GitHub) | Free for personal use |
| **Recommended for** | Open source, demos, portfolio | Internal projects, client work |

### Security — Files that should NEVER be committed

```bash
# Verify these are NOT in the repo (.gitignore excludes them)
git status  # These should NOT appear:

# ✗ infra/Pulumi.dev.yaml       → Pulumi secrets
# ✗ .env                        → environment variables
# ✗ appsettings.Development.json → local config
# ✗ launchSettings.json         → local URLs
```

**If you accidentally committed a secret:**

```bash
# Remove from history (BEFORE pushing)
git rm --cached infra/Pulumi.dev.yaml
git commit --amend --no-edit

# If you already pushed — rotate the secret IMMEDIATELY
pulumi config set sqlPassword "NEW_PASSWORD" --secret
```

### Daily workflow

```bash
git pull origin main
git checkout -b feature/new-feature
git add .
git commit -m "feat: description of the change"
git push origin feature/new-feature
gh pr create --title "feat: new feature" --body "Description of the change"
```

---

## Getting Started

### Prerequisites

```bash
# Verify all tools are installed
dotnet --version        # → 10.x
pulumi version          # → 3.x
az --version            # → 2.x
docker --version        # → 24.x+
```

### First-time Setup

**1. Clone and restore**

```bash
git clone <repo-url>
cd azure-minimal-api-net
dotnet restore
```

**2. Create local dev settings**

```json
// src/Doctors.Api/Properties/launchSettings.json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

**3. Initialize Pulumi (first time only)**

```bash
cd infra
pulumi login --local
pulumi stack init dev

# Set config (replace values)
pulumi config set env dev
pulumi config set sqlAdmin doctorsadmin
pulumi config set tenantId "<your-azure-tenant-id>"
pulumi config set location westus2
pulumi config set imageTag latest

# Set SQL password as encrypted secret
pulumi config set --secret sqlPassword
# → Enter a strong password: uppercase + lowercase + number + symbol, 8+ chars
```

**4. Purge old Key Vault (if exists from previous deployment)**

```bash
# Check if a soft-deleted Key Vault exists
az keyvault list-deleted --query "[?name=='kv-doctors-api-dev']" -o table

# If found, purge it (may take 1-2 minutes)
az keyvault purge --name kv-doctors-api-dev --location westus2
```

### Run Locally

```bash
# Uses InMemory database (no SQL Server needed)
dotnet run --project src/Doctors.Api
```

| URL | What |
|-----|------|
| `http://localhost:5000/api/doctors` | CRUD endpoints |
| `http://localhost:5000/swagger` | Swagger UI |
| `http://localhost:5000/redoc.html` | ReDoc documentation |

**Test the API:**

```bash
# GET all (empty at first)
curl http://localhost:5000/api/doctors

# CREATE a doctor
curl -X POST http://localhost:5000/api/doctors \
  -H "Content-Type: application/json" \
  -d '{"firstName":"Juan","lastName":"Pérez","licenseNumber":"MP-12345","specialty":"Cardiología"}'
```

### Deploy to Azure

**Using deploy.ps1 (recommended):**

```powershell
# Full deploy (infra + build + push + container app creation)
.\deploy.ps1

# With specific tag
.\deploy.ps1 -Tag "1.2.0"

# The script:
#   1. Creates/updates infra via Pulumi (RG, ACR, SQL, KV, CAE, etc.)
#   2. Builds Docker image
#   3. Pushes to ACR
#   4. Creates Container App via Azure CLI (with KV secret)
#   5. Configures SQL firewall rule for Container App outbound IP
#   6. Verifies API is responding
```

**Manual (step by step):**

```powershell
# 1. Create/update infrastructure (no Container App)
pulumi up --cwd infra --yes

# 2. Build Docker image
docker build -t acrdoctorsapidev.azurecr.io/doctors-api:latest .

# 3. Push to ACR
az acr login --name acrdoctorsapidev
docker push acrdoctorsapidev.azurecr.io/doctors-api:latest

# 4. Get KV secret
$SQL_CONN = az keyvault secret show --vault-name kv-doctors-api-dev --name "sql-conn-dev-westus2" --query "value" -o tsv

# 5. Create Container App
az containerapp create `
    --name ca-doctors-api-dev `
    --resource-group rg-doctors-dev `
    --environment cae-doctors-api-dev `
    --image acrdoctorsapidev.azurecr.io/doctors-api:latest `
    --target-port 8080 `
    --ingress external `
    --min-replicas 1 --max-replicas 5 `
    --cpu 0.25 --memory "0.5Gi" `
    --registry-server "acrdoctorsapidev.azurecr.io" `
    --env-vars ASPNETCORE_ENVIRONMENT=Development "ConnectionStrings__DefaultConnection=secretref:sql-connection-string" `
    --secrets "sql-connection-string=$SQL_CONN"

# 6. Get URL
az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-dev --query "properties.configuration.ingress.fqdn" -o tsv
```

**Change image tag only (no infra changes):**

```powershell
pulumi config set imageTag "v1.2.3" --cwd infra
.\deploy.ps1 -Tag "v1.2.3"
```

### Validate Deployment

```powershell
# Get current URL
$URL = "https://$(az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-api-dev --query 'properties.configuration.ingress.fqdn' -o tsv)"

# Health check
curl "$URL/api/doctors"

# Swagger UI
curl "$URL/swagger"

# Create a doctor
curl -X POST "$URL/api/doctors" `
  -H "Content-Type: application/json" `
  -d '{"firstName":"María","lastName":"González","licenseNumber":"MP-67890","specialty":"Neurología"}'
```

### Destroy & Recreate Infrastructure

> ⚠️ **DANGER** — This section destroys ALL Azure resources for the project.
> You will lose: databases, secrets, Docker images, logs. **This cannot be undone.**
>
> Only use this if you need a full environment reset (e.g., corrupted Pulumi state,
> accumulated changes you don't want to resolve one by one, or starting fresh).

#### Option 1: `pulumi destroy` (recommended — clean destruction)

```powershell
# Destroy all resources created by Pulumi
pulumi destroy --cwd infra --yes

# Purge Key Vault (soft-delete retains it for 90 days)
az keyvault purge --name kv-doctors-api-dev --no-wait

# Verify everything was deleted
az group show --name rg-doctors-dev  # Should return ResourceNotFound
```

> `pulumi destroy` deletes resources from Azure AND removes them from Pulumi state.
> Key Vault needs a separate purge because of Azure soft-delete policy.

#### Option 2: Delete the Resource Group (faster, less clean)

```powershell
# ⚠️ THIS DELETES EVERYTHING — including resources Pulumi doesn't manage
az group delete --name rg-doctors-dev --yes --no-wait

# Purge Key Vault
az keyvault purge --name kv-doctors-api-dev --no-wait

# Wait for completion (may take 2-5 minutes)
az group show --name rg-doctors-dev  # ResourceNotFound = done
```

> **Difference:** `pulumi destroy` is surgical (deletes only Pulumi-managed resources).
> `az group delete` is nuclear (deletes EVERYTHING in the group, including manual resources).

#### Recreate after destroy

```powershell
pulumi up --cwd infra --yes   # Create infrastructure
.\deploy.ps1                  # Build, push, and deploy the app
```

> **If Pulumi state is corrupted** (resources exist in Azure but state is broken):
> ```powershell
> pulumi state delete --all --cwd infra --yes      # Clear state
> az group delete --name rg-doctors-dev --yes      # Delete old resources
> az keyvault purge --name kv-doctors-api-dev      # Purge soft-deleted KV
> pulumi up --cwd infra --yes                      # Create fresh
> ```

---

## API Endpoints

| Method | Path | Description | Request Body | Response |
|--------|------|-------------|-------------|----------|
| `GET` | `/api/doctors` | List all active doctors | — | `200` + `DoctorDto[]` |
| `GET` | `/api/doctors/{id}` | Get doctor by ID | — | `200` + `DoctorDto` or `404` |
| `POST` | `/api/doctors` | Create a doctor | `CreateDoctorRequest` | `201` + `DoctorDto` or `400`/`409` |
| `PUT` | `/api/doctors/{id}` | Update a doctor | `UpdateDoctorRequest` | `200` + `DoctorDto` or `400`/`404` |
| `DELETE` | `/api/doctors/{id}` | Soft-delete a doctor | — | `204` or `404` |

### Request/Response Examples

**POST /api/doctors**
```json
// Request
{
  "firstName": "Juan",
  "lastName": "Pérez",
  "licenseNumber": "MP-12345",
  "specialty": "Cardiología",
  "email": "juan@hospital.com",
  "phone": "+54 11 1234-5678"
}

// Response 201
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "firstName": "Juan",
  "lastName": "Pérez",
  "licenseNumber": "MP-12345",
  "specialty": "Cardiología",
  "email": "juan@hospital.com",
  "phone": "+54 11 1234-5678",
  "isActive": true,
  "createdAt": "2026-04-02T18:00:00Z"
}
```

**Error 409 (duplicate license)**
```json
{
  "title": "Conflict",
  "status": 409,
  "detail": "Doctor with license 'MP-12345' already exists.",
  "instance": "/api/doctors"
}
```

---

## Configuration

### Environment Variables (Container App)

| Variable | Value | Purpose |
|----------|-------|---------|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Enables Swagger/ReDoc |
| `ConnectionStrings__DefaultConnection` | Secret ref → `sql-connection-string` | Azure SQL connection (via Container App secret) |

> **Note:** Use double underscore (`__`) not colon (`:`) for nested config in environment variables.

### Pulumi Stack Config (infra/Pulumi.dev.yaml)

```yaml
config:
  doctors-api-infra:env: dev
  doctors-api-infra:sqlAdmin: doctorsadmin
  doctors-api-infra:tenantId: "<your-tenant-id>"
  doctors-api-infra:location: westus2
  doctors-api-infra:imageTag: latest          # Docker image tag
  doctors-api-infra:sqlPassword:
    secure: <encrypted>
```

---

## Troubleshooting

### Quick Diagnostics

```powershell
# Check Container App status
az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-dev --query "{status: properties.runningStatus, revision: properties.latestRevisionName, fqdn: properties.configuration.ingress.fqdn}" -o table

# Check revision status
az containerapp revision list --name ca-doctors-api-dev --resource-group rg-doctors-dev --query "[].{name: name, active: properties.active, health: properties.healthState}" -o table

# Check container logs (last 30 lines)
az containerapp logs show --name ca-doctors-api-dev --resource-group rg-doctors-dev --type console --tail 30

# Check system logs (infrastructure errors)
az containerapp logs show --name ca-doctors-api-dev --resource-group rg-doctors-dev --type system --tail 20
```

### Common Errors and Solutions

#### Error: SQL Error 40615 — "Cannot open server"

**Symptom:** Container App starts but crashes. Logs show `Number:40615,State:1,Class:14`.

**Cause:** SQL Server firewall is blocking the Container App. The `AllowAzureServices` alias (0.0.0.0) **does NOT cover Container Apps** — Container Apps uses different dynamic outbound IPs.

**Solution:**
```powershell
# 1. Check Container App outbound IP
az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-dev --query "properties.outboundIpAddresses" -o tsv

# 2. Check current firewall rules
az sql server firewall-rule list --server sql-doctors-api-dev --resource-group rg-doctors-dev -o table

# 3. If the IP is missing, re-run deploy.ps1 to create the firewall rule
.\deploy.ps1
```

#### Error: Pulumi "ResourceNotFound" — Key Vault or Container App doesn't exist

**Symptom:** `Status=404 Code="ResourceNotFound"` when running `pulumi up`.

**Cause:** The resource was deleted from Azure but still exists in Pulumi state (state drift).

**Solution:**
```powershell
# 1. Check if KV is in soft-delete
az keyvault list-deleted --query "[?name=='kv-doctors-api-dev']" -o table

# 2. If in soft-delete: purge
az keyvault purge --name kv-doctors-api-dev --location westus2

# 3. Remove resource from Pulumi state
pulumi state delete "<urn>" --cwd infra --yes

# To find the URN:
pulumi stack export --cwd infra | Select-String "urn"

# 4. Re-deploy
pulumi up --cwd infra --yes
```

#### Error: Container App won't start — "Persistent Failure to start container"

**Symptom:** Logs show `ContainerBackOff`, `startup probe failed: connection refused`.

**Cause:** Could be:
1. Image doesn't exist in ACR (wrong tag)
2. SQL connection error (see 40615 above)
3. API crashes on startup (check console logs)

**Diagnosis:**
```powershell
# Verify image exists in ACR
az acr repository show-tags --name acrdoctorsapidev --repository doctors-api -o table

# Check container console logs (shows actual .NET error)
az containerapp logs show --name ca-doctors-api-dev --resource-group rg-doctors-dev --type console --tail 30

# Restart current revision
az containerapp revision restart --name ca-doctors-api-dev --resource-group rg-doctors-dev --revision <revision-name>
```

#### Error: `MANIFEST_UNKNOWN` — image not found in ACR

**Symptom:** `manifest tagged by "{tag}" is not found` when creating/updating Container App.

**Cause:** The tag configured in `imageTag` doesn't exist in ACR. `deploy.ps1` creates the image with `$Tag`, but `pulumi up` uses the tag from `Pulumi.dev.yaml`.

**Solution:**
```powershell
# Check existing tags
az acr repository show-tags --name acrdoctorsapidev --repository doctors-api -o table

# Sync the tag
pulumi config set imageTag "latest" --cwd infra   # or whatever tag exists
# or
.\deploy.ps1 -Tag "dev-0.1.0"                      # push with the configured tag
```

#### Error: ACR `UnAuthorizedForCredentialOperations`

**Symptom:** `Cannot perform credential operations as admin user is disabled`.

**Cause:** The code tries to read ACR admin credentials (`listRegistryCredentials`) but admin is disabled.

**Solution:**
```powershell
# Enable admin temporarily
az acr update -n acrdoctorsapidev --admin-enabled true

# Then run pulumi up
pulumi up --cwd infra --yes
```

#### Error: Key Vault purge timeout

**Symptom:** `az keyvault purge` takes longer than 5 minutes.

**Cause:** Azure Key Vault purge is slow by design (cross-region replication).

**Solution:**
```powershell
# Use --no-wait
az keyvault purge --name kv-doctors-api-dev --location westus2 --no-wait

# Wait 2-3 minutes and verify
Start-Sleep -Seconds 180
az keyvault list-deleted --query "[?name=='kv-doctors-api-dev']" -o table
```

#### Error: `containerAppUrl` shows as `[secret]`

**Symptom:** `pulumi stack output` shows `containerAppUrl: [secret]`.

**Cause:** The output comes from `containerApp.Configuration` which contains registry credentials. Pulumi propagates the secret flag.

**Solution:** Already fixed — uses `containerApp.LatestRevisionFqdn` instead of `containerApp.Configuration`. If it persists, run `pulumi up` to update.

#### Error: DiagnosticSetting `SQLSecurityAuditEvents` not supported

**Symptom:** `Category 'SQLSecurityAuditEvents' is not supported` when creating diag-sql.

**Cause:** SQL log categories are at **database** level, not **server** level. `ResourceUri` must point to `sqlDatabase.Id`, not `sqlServer.Id`.

**Solution:** Already fixed in code. The `DiagnosticSetting` targets the database.

#### Error: Key Vault 403 Forbidden — "Caller is not authorized to perform action"

**Symptom:** `403 Forbidden` when Pulumi tries to create/read/delete secrets in Key Vault. Error mentions `ForbiddenByRbac`.

**Cause:** The Key Vault has `EnableRbacAuthorization = true` but the user/service principal running Pulumi has no RBAC role assigned on the vault. The `KeyVaultSecretsUser` role was only assigned to the Container App's managed identity.

**Solution:**
```powershell
# Assign "Key Vault Secrets Officer" to your user (allows create/delete secrets)
$USER_ID = az ad signed-in-user show --query "id" -o tsv
$KV_ID = az keyvault show --name kv-doctors-api-dev --resource-group rg-doctors-dev --query "id" -o tsv

az role assignment create --role "Key Vault Secrets Officer" --assignee $USER_ID --scope $KV_ID

# Wait 1-5 minutes for RBAC propagation, then retry
Start-Sleep -Seconds 120
pulumi destroy --cwd infra --yes
```

> **Tip:** For production, add this role assignment in `infra/Program.cs` so it's always present.

#### Error: Pulumi `RoleDefinitionDoesNotExist`

**Symptom:** `The specified role definition with ID '...' does not exist`.

**Cause:** The role definition ID is incorrect. The correct IDs are:
- AcrPull: `7f951dda-4ed3-4680-a7ca-43fe172d538d`
- KeyVaultSecretsUser: `4633458b-17de-408a-b874-0445c86b69e6`

**Solution:** Verify these IDs are in `infra/Program.cs`. They are already corrected.

#### Error: Container App shows old image after push

**Symptom:** You ran `docker push` but the Container App still runs the old image.

**Cause:** Container Apps does NOT auto-pull. You must force an update.

**Solution:**
```powershell
az containerapp update --name ca-doctors-api-dev --resource-group rg-doctors-dev --image acrdoctorsapidev.azurecr.io/doctors-api:latest
```

### Verify All Resources

```powershell
# Resource Group
az group show --name rg-doctors-dev --query "{name: name, location: location, tags: tags}" -o table

# ACR
az acr show --name acrdoctorsapidev --query "{name: name, tier: sku.tier, adminEnabled: adminUserEnabled}" -o table

# SQL
az sql server show --name sql-doctors-api-dev --resource-group rg-doctors-dev --query "{name: name, version: version}" -o table
az sql db show --name sqldb-doctors-dev --server sql-doctors-api-dev --resource-group rg-doctors-dev --query "{name: name, status: status, maxSize: maxSizeBytes}" -o table

# Key Vault
az keyvault show --name kv-doctors-api-dev --query "{name: name, rbacEnabled: properties.enableRbacAuthorization}" -o table

# Container App
az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-dev --query "{name: name, status: properties.runningStatus, fqdn: properties.configuration.ingress.fqdn}" -o table

# Pulumi outputs
pulumi stack output --cwd infra
```

---

## Known Issues

| Issue | Cause | Workaround |
|-------|-------|------------|
| **eastus2 SQL provisioning restricted** | Azure region limitation | Use `westus2` or `eastus` |
| **`Microsoft.AspNetCore.OpenApi` broken on .NET 10** | Pre-release package | Use NSwag.AspNetCore instead |
| **Swashbuckle 7.x incompatible with .NET 10** | `TypeLoadException` | Use NSwag.AspNetCore instead |
| **Docker build fails with Windows paths** | `obj/` contains `C:\...` paths | Use `**/obj/` (glob) in `.dockerignore` |
| **Key Vault name already taken** | Soft-delete retains names 90 days | `az keyvault purge --name <name>` |
| **Container App serves old image** | No auto-pull on push | Must call `az containerapp update` |
| **Swagger 404 on Azure** | Production mode disables it | Set `ASPNETCORE_ENVIRONMENT=Development` |
| **SQL firewall blocks Container App** | `AllowAzureServices` doesn't cover CA | Firewall rule with CA outbound IP |
| **EF migrations fail outside DI** | No provider configured | Use `IDesignTimeDbContextFactory` |
| **Key Vault purge timeout** | Azure replication delay | Use `--no-wait`, wait 2-3 min |
| **SQL diag categories at database level** | Server level only has metrics | `ResourceUri = sqlDatabase.Id` |
| **KV Secret 409 Conflict on recreate** | Secret soft-delete retains name 90 days | Dynamic name: `sql-conn-{env}-{location}` |
| **Key Vault 403 on destroy** | RBAC-enabled KV requires user role | `az role assignment create --role "Key Vault Secrets Officer"` |
| **Container App not created by Pulumi** | Image doesn't exist when Pulumi runs | Container App created via `deploy.ps1` after build+push |
| **`az containerapp create` --secret-env-vars error** | Flag doesn't exist | Use `secretref:` prefix in `--env-vars` |
| **Pulumi.AzureNative.Insights namespace missing** | Removed in v3.x | Use `AzureNative.Monitor` instead |
| **Only 1 Container App Environment per region** | Azure subscription limit | Delete old CAE or use different region |

---

## License

Internal project — demo/learning purposes.
