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
                         │  Endpoints/     ← Routes + Auth     │
                         │  Services/      ← TokenService      │
                         │  DTOs/          ← Request/Response  │
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
| **Auth** | Microsoft.AspNetCore.Authentication.JwtBearer | 10.0 | JWT Bearer auth with custom issuer |
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
│       ├── Program.cs                     # Composition root + JWT auth config
│       ├── appsettings.Development.json.example  # ← Copy to appsettings.Development.json for local JWT config
│       ├── Endpoints/
│       │   ├── DoctorEndpoints.cs         # 5 CRUD route handlers (mutations require auth)
│       │   └── AuthEndpoints.cs           # POST /auth/login → JWT token
│       ├── Services/
│       │   └── TokenService.cs            # JWT generation (HMAC-SHA256)
│       ├── DTOs/
│       │   ├── LoginRequest.cs            # Login request (Username, Password)
│       │   └── TokenResponse.cs           # Login response (Token, ExpiresAt)
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
│   ├── Pulumi.dev.yaml                    # Stack config (secrets encrypted, gitignored)
│   ├── Pulumi.dev.yaml.example            # ← Copy this to Pulumi.dev.yaml and fill in values
│   ├── infra.csproj                       # Pulumi project
│   └── Program.cs                         # Azure resource definitions (documented in Spanish)
│
├── Dockerfile                             # Multi-stage: SDK 10.0 → ASP.NET 10.0
├── .dockerignore                          # Excludes bin/, obj/, infra/
├── .env.example                           # ← Copy to .env for CI/CD (optional)
├── deploy.ps1                             # One-command deploy script (with pre-checks)
├── Directory.Build.props                  # Shared: net10.0, LangVersion 14
└── azure-minimal-api-net.slnx             # Solution file (.NET 10 format)
```

### Files NOT in repo (security)

| File | Why | How to create |
|------|-----|---------------|
| `infra/Pulumi.dev.yaml` | Contains encrypted secrets (SQL password) | Copy from `infra/Pulumi.dev.yaml.example` → see [First-time Setup](#first-time-setup) |
| `src/Doctors.Api/appsettings.Development.json` | Local JWT config (SigningKey) | Copy from `src/Doctors.Api/appsettings.Development.json.example` → see [First-time Setup](#first-time-setup) |
| `.env` | Local environment variables | Copy from `.env.example` (optional — deploy.ps1 prompts interactively) |
| `src/Doctors.Api/Properties/launchSettings.json` | Local dev settings | Auto-created by IDE, or see setup below |

> **Tip**: The repo includes `.example` files for every gitignored config. After cloning:
> ```bash
> cp infra/Pulumi.dev.yaml.example infra/Pulumi.dev.yaml
> cp src/Doctors.Api/appsettings.Development.json.example src/Doctors.Api/appsettings.Development.json
> cp .env.example .env
> ```

---

## Infrastructure

### Multi-Stage Deployment Architecture

The project uses a **Two-Tier Infrastructure** strategy via Pulumi `StackReference` to separate global resources from environment-specific ones. This ensures cost-efficiency, avoids global naming conflicts, and provides stability across stages.

  ```mermaid
  graph TD
      subgraph Shared[infra-shared / main]
          RG_Shared[rg-core-shared]
          ACR[acrfcoremain<br>Azure Container Registry]
          LAW[law-core-main<br>Log Analytics Workspace]
          CAE_Shared[cae-core-main<br>Container App Environment]
          RG_Shared --> ACR
          RG_Shared --> LAW
          RG_Shared --> CAE_Shared
          CAE_Shared -. "sends logs to" .-> LAW
      end

      subgraph Dev[infra / dev]
          RG_Dev[rg-doctors-dev]
          SQL_Dev[(sql-doctors-api-dev<br>SQL Serverless<br>60m auto-pause)]
          DB_Dev[(sqldb-doctors-dev)]
          KV_Dev[kv-doctors-api-dev<br>Key Vault + Secrets]
          CA_Dev[ca-doctors-api-dev<br>ASPNETCORE_ENVIRONMENT=Development]
          DIAG_Dev[diag-sql-dev + diag-kv-dev<br>DiagnosticSettings *]

          RG_Dev --> SQL_Dev
          SQL_Dev --> DB_Dev
          RG_Dev --> KV_Dev
          CAE_Shared --> CA_Dev
          CA_Dev -. "reads secrets" .-> KV_Dev
      end

      subgraph QA[infra / qa]
          RG_QA[rg-doctors-qa]
          SQL_QA[(sql-doctors-api-qa<br>SQL Serverless<br>60m auto-pause)]
          DB_QA[(sqldb-doctors-qa)]
          KV_QA[kv-doctors-api-qa<br>Key Vault + Secrets]
          CA_QA[ca-doctors-api-qa<br>ASPNETCORE_ENVIRONMENT=Production]
          DIAG_QA[diag-sql-qa + diag-kv-qa<br>DiagnosticSettings *]

          RG_QA --> SQL_QA
          SQL_QA --> DB_QA
          RG_QA --> KV_QA
          CAE_Shared --> CA_QA
          CA_QA -. "reads secrets" .-> KV_QA
      end

      subgraph Prod[infra / prod]
          RG_Prod[rg-doctors-prod]
          SQL_Prod[(sql-doctors-api-prod<br>SQL Serverless<br>no auto-pause)]
          DB_Prod[(sqldb-doctors-prod)]
          KV_Prod[kv-doctors-api-prod<br>Key Vault + Secrets]
          CA_Prod[ca-doctors-api-prod<br>ASPNETCORE_ENVIRONMENT=Production]
          DIAG_Prod[diag-sql-prod + diag-kv-prod<br>DiagnosticSettings *]

          RG_Prod --> SQL_Prod
          SQL_Prod --> DB_Prod
          RG_Prod --> KV_Prod
          CAE_Shared --> CA_Prod
          CA_Prod -. "reads secrets" .-> KV_Prod
      end

      CA_Dev -. "pulls image" .-> ACR
      CA_QA -. "pulls image" .-> ACR
      CA_Prod -. "pulls image" .-> ACR
      DIAG_Dev -. "logs" .-> LAW
      DIAG_QA -. "logs" .-> LAW
      DIAG_Prod -. "logs" .-> LAW
  ```

> `*` DiagnosticSettings solo se crean cuando `costMode` es `normal` o `full`. En `nano`/`mini` se omiten para ahorrar ~$2-5/mes.

  1.  **Shared Infrastructure (`infra-shared` / `main`)**:
      *   **Purpose**: Resources shared across all environments to avoid duplication, reduce costs, bypass Azure subscription limits (e.g., 1 CAE per region), and avoid global Azure naming conflicts.
      *   **Resources**: `rg-core-shared` (Resource Group), `acrfcoremain` (Container Registry, Basic SKU), `law-core-main` (Log Analytics Workspace), `cae-core-main` (Container App Environment).
  2.  **Environment Infrastructure (`infra` / `{env}`)**:
      *   **Purpose**: Fully isolated resources per stage (`dev`, `qa`, `prod`).
      *   **Resources per env**:
          *   `rg-doctors-{env}` - Resource Group
          *   `sql-doctors-api-{env}` + `sqldb-doctors-{env}` - SQL Serverless (auto-pause 60min en dev/qa, siempre activo en prod)
          *   `kv-doctors-api-{env}` - Key Vault (RBAC) con secrets: connection string + JWT signing key
          *   `ca-doctors-api-{env}` - Container App (Dev: `ASPNETCORE_ENVIRONMENT=Development`, QA/Prod: `Production`)
          *   `diag-sql-{env}` + `diag-kv-{env}` - DiagnosticSettings -> Log Analytics (solo en `costMode: normal/full`)
      *   **Reference**: Usa `StackReference("organization/azure-minimal-api-net-shared/main")` para obtener ACR, LAW y el CAE del shared stack.

### Deploying to Different Environments

Deployment is fully orchestrated via `deploy.ps1`. The script includes a **Fail-Fast** mechanism to verify that shared dependencies exist before starting.

*   **Development**: `.\deploy.ps1 -Env dev`
*   **Quality Assurance**: `.\deploy.ps1 -Env qa`
*   **Production**: `.\deploy.ps1 -Env prod`

*(Note: QA and Prod enforce `ASPNETCORE_ENVIRONMENT=Production` which safely disables Swagger and optimizes the API).*

### First-Time Setup

If you are deploying for the exact first time, you must create the shared stack first. You can do this in one command by appending the `-DeployShared` flag:

```powershell
# 1. Initialize the shared stack (first time only)
cd infra-shared
pulumi stack init main
cd ..

# 2. Initialize your target environment stack
cd infra
pulumi stack init dev
cd ..

# 3. Deploy everything (Shared + Env)
.\deploy.ps1 -DeployShared -Env dev
```

### Resources by Cost Mode

| Resource | nano ($3-5) | mini ($5-8) | normal ($10-15) | full ($25-40) |
|----------|:-----------:|:-----------:|:---------------:|:-------------:|
| Resource Group | ✅ | ✅ | ✅ | ✅ |
| Container App | 0-1 replicas | 0-2 replicas | 1-5 replicas | 2-10 replicas |
| Container App CPU/RAM | 0.25 / 0.5Gi | 0.25 / 0.5Gi | 0.25 / 0.5Gi | 0.5 / 1Gi |
| Container Registry (ACR Basic) | ✅ | ✅ | ✅ | ✅ |
| SQL Server | ✅ | ✅ | ✅ | ✅ |
| SQL Database (Serverless) | 1 GB | 2 GB | 2 GB | 5 GB |
| Key Vault (RBAC) | ✅ | ✅ | ✅ | ✅ |
| Log Analytics | ✅ | ✅ | ✅ | ✅ |
| Container App Environment | ✅ | ✅ | ✅ | ✅ |
| **DiagnosticSettings** | ❌ | ❌ | ✅ | ✅ |
| **RBAC roles** (AcrPull, KV User) | ✅ | ✅ | ✅ | ✅ |

> **Key difference**: `nano` y `mini` desactivan DiagnosticSettings para ahorrar ~$2-5/mes.
> `normal` y `full` los activan para tener logs de SQL y Key Vault en Log Analytics.

### Data Flow

```
1. Request → Container App (HTTPS, external ingress via Container App Environment)
2. Container App → ACR: Pull Docker image (via SystemAssigned MI + AcrPull role)
3. Container App → SQL: Query data (connection string from CA secret, firewall allows CA outbound IP)
4. Container App → Key Vault: Read secrets at deploy time (via MI + KeyVaultSecretsUser role)
5. SQL Database + Key Vault → Log Analytics: Diagnostic logs (if costMode = normal/full)
```

> **infra/Program.cs** is fully documented with 16 sections explaining architecture, each resource, security, and costs. Read the file to understand each decision.

### Security Features

| Feature | Implementation | Cost |
|---------|---------------|------|
| **JWT Authentication** | Custom issuer, HMAC-SHA256, 60min expiry | $0 |
| **Endpoint Protection** | `.RequireAuthorization()` on POST/PUT/DELETE | $0 |
| **Swagger Security** | NSwag Bearer scheme → Authorize button | $0 |
| **Managed Identity** | SystemAssigned on Container App | $0 |
| **ACR Pull via RBAC** | AcrPull role (admin disabled) | $0 |
| **Key Vault RBAC** | EnableRbacAuthorization = true | $0 |
| **Secret Reference** | Connection string + JWT key via SecretRef (no plaintext) | $0 |
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
| SQL Firewall | AllowAzureServices (0.0.0.0) | VNet + Private Endpoint (~$30-50/mo) |
| SQL Auth | Admin password | Azure AD auth |
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

| File/Folder | Reason | `.example` included? |
|-------------|--------|:-------------------:|
| `bin/`, `obj/` | .NET build artifacts | — |
| `.vs/` | Visual Studio config | — |
| `infra/Pulumi.dev.yaml` | Contains encrypted secrets (SQL password) | ✅ `infra/Pulumi.dev.yaml.example` |
| `appsettings.Development.json` | Local JWT config (signing key) | ✅ `src/Doctors.Api/appsettings.Development.json.example` |
| `.env` | Local environment variables | ✅ `.env.example` |

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

# These SHOULD appear (.example files are safe to commit):
# ✓ infra/Pulumi.dev.yaml.example
# ✓ src/Doctors.Api/appsettings.Development.json.example
# ✓ .env.example
```

**If you accidentally committed a secret:**

```bash
# Remove from history (BEFORE pushing)
git rm --cached infra/Pulumi.dev.yaml
git commit --amend --no-edit

# If you already pushed — rotate the secret IMMEDIATELY
pulumi config set --secret doctors-api-infra:sqlPassword "NEW_PASSWORD"
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

**2. Copy config from examples**

```bash
# Copy example files (REQUIRED — app won't start without these)
cp infra/Pulumi.dev.yaml.example infra/Pulumi.dev.yaml
cp src/Doctors.Api/appsettings.Development.json.example src/Doctors.Api/appsettings.Development.json

# Optional: for CI/CD non-interactive deploy
cp .env.example .env
```

**3. Create local dev settings**

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

**4. Initialize Pulumi (first time only)**

```bash
cd infra
pulumi login --local
pulumi stack init dev

# Set non-secret config values
pulumi config set doctors-api-infra:env dev
pulumi config set doctors-api-infra:sqlAdmin doctorsadmin
pulumi config set doctors-api-infra:tenantId "<your-azure-tenant-id>"
pulumi config set doctors-api-infra:location westus2
pulumi config set doctors-api-infra:imageTag latest

# Set SQL password as encrypted secret (you'll be prompted to enter the value)
pulumi config set --secret doctors-api-infra:sqlPassword "tu-password-aqui"
# → Strong password: uppercase + lowercase + number + symbol, 8+ chars

# Set JWT signing key as encrypted secret (≥ 32 characters)
pulumi config set --secret doctors-api-infra:jwtSigningKey "tu-jwt-key-aqui"
# → Strong random key, e.g.: openssl rand -base64 48
```

**5. Run deploy**

```powershell
.\deploy.ps1
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
# GET all (empty at first) — public, no auth needed
curl http://localhost:5000/api/doctors

# Get a JWT token
curl -s -X POST http://localhost:5000/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin123"}' | jq .token

# CREATE a doctor (requires Bearer token)
curl -X POST http://localhost:5000/api/doctors \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d '{"firstName":"Juan","lastName":"Pérez","licenseNumber":"MP-12345","specialty":"Cardiología"}'

# Without token → 401 Unauthorized
curl -X POST http://localhost:5000/api/doctors \
  -H "Content-Type: application/json" \
  -d '{"firstName":"Test"}'
```

### Deploy to Azure

```powershell
# Deploy to dev (requires -Env parameter now)
.\deploy.ps1 -Env dev

# Deploy with version tag
.\deploy.ps1 -Env dev -Tag "1.2.0"

# Deploy to production (using shared ACR)
.\deploy.ps1 -Env prod -Tag "1.0.0"
```

El script hace todo automáticamente:
1. Crea/actualiza infraestructura compartida (si `-DeployShared` es usado).
2. Crea/actualiza infraestructura del entorno (RG, SQL Serverless, KV, CAE, Container App).
3. Construye la imagen Docker.
4. Push a Azure Container Registry (Shared).
5. Actualiza el Container App con la nueva imagen.
6. Verifica que la API responda.

**Cambiar solo la imagen (sin cambios de infra):**

```powershell
.\deploy.ps1 -Env dev -Tag "v1.2.3"
```

### Validate Deployment

```powershell
# Get the URL
$URL = "https://$(az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-dev --query 'properties.configuration.ingress.fqdn' -o tsv)"

# Health check
curl "$URL/api/doctors"

# Get auth token and create a doctor
$TOKEN = (curl -s -X POST "$URL/auth/login" -H "Content-Type: application/json" -d '{"username":"admin","password":"admin123"}' | ConvertFrom-Json).token

curl -X POST "$URL/api/doctors" `
  -H "Authorization: Bearer $TOKEN" `
  -H "Content-Type: application/json" `
  -d '{"firstName":"María","lastName":"González","licenseNumber":"MP-67890","specialty":"Neurología"}'
```

### Destroy & Recreate Infrastructure

```powershell
# Destruir entorno dev (requiere confirmación)
.\destroy-all.ps1 -Env dev

# Destruir entorno + infraestructura compartida (peligroso en prod)
.\destroy-all.ps1 -Env dev -DestroyShared

# Recrear desde cero
.\deploy.ps1 -DeployShared -Env dev
```

> `destroy-all.ps1` ejecuta `pulumi destroy` y luego limpia automáticamente
> recursos soft-deleted (Key Vault) que Azure retiene por 90 días.

---

## API Endpoints

### Authentication

| Method | Path | Description | Request Body | Response |
|--------|------|-------------|-------------|----------|
| `POST` | `/auth/login` | Get JWT token | `LoginRequest` | `200` + `TokenResponse` or `401` |

### Doctors (CRUD)

| Method | Path | Description | Auth Required | Request Body | Response |
|--------|------|-------------|:------------:|-------------|----------|
| `GET` | `/api/doctors` | List all active doctors | ❌ No | — | `200` + `DoctorDto[]` |
| `GET` | `/api/doctors/{id}` | Get doctor by ID | ❌ No | — | `200` + `DoctorDto` or `404` |
| `POST` | `/api/doctors` | Create a doctor | ✅ Yes | `CreateDoctorRequest` | `201` + `DoctorDto` or `400`/`409` |
| `PUT` | `/api/doctors/{id}` | Update a doctor | ✅ Yes | `UpdateDoctorRequest` | `200` + `DoctorDto` or `400`/`404` |
| `DELETE` | `/api/doctors/{id}` | Soft-delete a doctor | ✅ Yes | — | `204` or `404` |

> **Auth strategy**: JWT Bearer with Custom Issuer (HMAC-SHA256). Only mutations (POST, PUT, DELETE) require auth. GETs remain public for dev/demo convenience. Cloud-portable — no dependency on Entra ID or external IdP.

### How to authenticate

```bash
# 1. Get a token (demo credentials: admin / admin123)
curl -X POST http://localhost:5000/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin123"}'

# Response: {"token":"eyJhbG...","expiresAt":"2026-04-06T..."}

# 2. Use the token for mutations
curl -X POST http://localhost:5000/api/doctors \
  -H "Authorization: Bearer eyJhbG..." \
  -H "Content-Type: application/json" \
  -d '{"firstName":"Juan","lastName":"Pérez","licenseNumber":"MP-12345","specialty":"Cardiología"}'

# 3. GETs work without token
curl http://localhost:5000/api/doctors
```

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

### JWT Settings (appsettings.Development.json)

> **Note**: This file is gitignored. Copy from the example:
> ```bash
> cp src/Doctors.Api/appsettings.Development.json.example src/Doctors.Api/appsettings.Development.json
> ```

```json
{
  "JwtSettings": {
    "Issuer": "doctors-api",
    "Audience": "doctors-api",
    "SigningKey": "<replace-with-min-32-char-random-key>",
    "ExpiryMinutes": 60
  }
}
```

| Field | Purpose | Default |
|-------|---------|---------|
| `Issuer` | Token issuer claim (validates where token came from) | `doctors-api` |
| `Audience` | Token audience claim (validates intended recipient) | `doctors-api` |
| `SigningKey` | HMAC-SHA256 symmetric key (≥ 32 chars) | Dev-only hardcoded |
| `ExpiryMinutes` | Token lifetime | `60` |

> **Production**: signing key stored in Azure Key Vault as `jwt-signing-key-dev` secret. `deploy.ps1` reads it and passes to Container App.

### Environment Variables (Container App)

| Variable | Value | Purpose |
|----------|-------|---------|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Enables Swagger/ReDoc |
| `ConnectionStrings__DefaultConnection` | Secret ref → `sql-connection-string` | Azure SQL connection |
| `JwtSettings__SigningKey` | Secret ref → `jwt-signing-key` | JWT token signing (from Key Vault) |

> **Note:** Use double underscore (`__`) not colon (`:`) for nested config in environment variables.

### Pulumi Stack Config (infra/Pulumi.dev.yaml)

> **Note**: This file is gitignored. Copy from the example:
> ```bash
> cp infra/Pulumi.dev.yaml.example infra/Pulumi.dev.yaml
> # Then set secrets:
> pulumi config set --secret doctors-api-infra:sqlPassword "tu-password-aqui"
> pulumi config set --secret doctors-api-infra:jwtSigningKey "tu-jwt-key-aqui"
> ```

```yaml
config:
  doctors-api-infra:env: dev
  doctors-api-infra:sqlAdmin: doctorsadmin
  doctors-api-infra:tenantId: "<your-tenant-id>"
  doctors-api-infra:location: westus2
  doctors-api-infra:imageTag: latest          # Docker image tag
  doctors-api-infra:sqlPassword:
    secure: <encrypted>
  doctors-api-infra:jwtSigningKey:
    secure: <encrypted>                       # JWT signing key (≥ 32 chars)
```

---

## Troubleshooting

### Quick Diagnostics

```powershell
# Check Container App status
az containerapp show --name ca-doctors-api-dev --resource-group rg-doctors-dev --query "{status: properties.runningStatus, revision: properties.latestRevisionName, fqdn: properties.configuration.ingress.fqdn}" -o table
```

### Advanced Infrastructure Fixes (Pulumi & Azure)

#### 1. Pulumi State Desync ("ResourceGroupNotFound" or similar 404s)
**Symptom**: Pulumi says `unchanged` or `created successfully`, but Azure throws `404 Not Found` when trying to assign roles or use resources.
**Cause**: You manually deleted a resource (e.g., `rg-core-shared`) via the Azure Portal, but Pulumi's local state still thinks it exists.
**Fix**: Force Pulumi to reconcile its state with Azure reality:
```powershell
cd infra-shared # or infra
pulumi refresh --stack main --yes
```
*(Note: `deploy.ps1` now runs `--refresh` automatically to prevent this!)*

#### 2. Key Vault 409 Conflict ("Already exists in deleted state")
**Symptom**: Pulumi fails to create `kv-doctors-api-dev` with a `409 ConflictError`.
**Cause**: Key Vaults have soft-delete enabled by default. Deleting a resource group does not permanently delete the vault; Azure holds the name hostage for 90 days.
**Fix**: Purge the vault manually from Azure's recycle bin:
```powershell
az keyvault purge --name kv-doctors-api-dev --location westus2 --no-wait
```

#### 3. Pulumi Destroy Fails on Key Vault Secrets ("Tenant not found" or Auth errors)
**Symptom**: `pulumi destroy` fails to delete `sql-conn-dev` or `jwt-signing-key-dev` because of AADSTS90002 or auth errors.
**Cause**: The secret was originally created with an incorrect `tenantId` in the Pulumi config. `pulumi destroy` uses the old, burned-in tenant ID from the state file, so fixing the config won't help.
**Fix**: Perform "surgery" on the Pulumi state to make it forget the secrets, then destroy the rest normally:
```powershell
cd infra
# Force Pulumi to forget the broken secrets
pulumi state delete "urn:pulumi:dev::doctors-api-infra::azure-native:keyvault:Secret::sql-conn-dev" --force --yes
pulumi state delete "urn:pulumi:dev::doctors-api-infra::azure-native:keyvault:Secret::jwt-signing-key-dev" --force --yes

cd ..
# Now destroy the rest (Azure will delete the secrets when it deletes the Key Vault)
.\destroy-all.ps1 -Env dev
```

#### 4. Pulumi Cipher Error ("message authentication failed")
**Symptom**: `error: validating stack config: cipher: message authentication failed`
**Cause**: The `Pulumi.dev.yaml` was edited manually or the passphrase was lost/changed, making the local state impossible to decrypt.
**Fix**: 
1. If the environment is expendable (like `dev`), delete the stack forcefully: `pulumi stack rm dev --force`
2. Re-init the stack (`pulumi stack init dev`), set the config values again, and run `.\deploy.ps1 -Env dev`.

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
