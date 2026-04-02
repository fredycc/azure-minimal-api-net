# Doctors API — Specialist Doctors CRUD

> CRUD API for managing specialist doctors. Built with .NET 10, Clean Architecture, deployed to Azure via Pulumi.

---

## Table of Contents

- [Architecture](#architecture)
- [Technologies](#technologies)
- [Project Structure](#project-structure)
- [Infrastructure](#infrastructure)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [First-time Setup](#first-time-setup)
  - [Run Locally](#run-locally)
  - [Deploy to Azure](#deploy-to-azure)
- [API Endpoints](#api-endpoints)
- [Configuration](#configuration)
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
│   └── Program.cs                         # Azure resource definitions
│
├── Dockerfile                             # Multi-stage: SDK 10.0 → ASP.NET 10.0
├── .dockerignore                          # Excludes bin/, obj/, infra/
├── deploy.ps1                             # One-command deploy script
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
┌──────────────────────────────────────────────────────────────────────┐
│  rg-doctors-api-dev                        Resource Group (westus2)  │
│                                                                      │
│  ├── acrdoctorsapidev                      Container Registry (Basic) │
│  │   └── doctors-api:latest                Docker image               │
│  │                                                                     │
│  ├── law-doctors-api-dev                   Log Analytics Workspace    │
│  │                                                                     │
│  ├── sql-doctors-api-dev                   SQL Server                 │
│  │   ├── sqldb-doctors-dev                 SQL Database (Serverless)  │
│  │   │   ├── AutoPauseDelay: 60 min       ← $0 when idle             │
│  │   │   ├── MinCapacity: 0.5 vCores      ← Minimum when active      │
│  │   │   └── MaxSize: 2 GB                ← Enough for dev/demo      │
│  │   └── Firewall: 0.0.0.0–0.0.0.0       ← Allow all Azure services  │
│  │                                                                     │
│  ├── kv-doctors-api-dev                    Key Vault                  │
│  │                                                                     │
│  ├── cae-doctors-api-dev                   Container App Environment  │
│  │   └── ca-doctors-api-dev               Container App               │
│  │       ├── Image: acrdoctorsapidev.azurecr.io/doctors-api:latest   │
│  │       ├── Port: 8080                                              │
│  │       ├── Env:                                                     │
│  │       │   ├── ASPNETCORE_ENVIRONMENT=Development                  │
│  │       │   └── ConnectionStrings__DefaultConnection=Server=...     │
│  │       └── Scale: 1–5 replicas (consumption)                       │
│  │                                                                     │
│  └── Outputs:                                                           │
│      ├── containerAppUrl: https://ca-doctors-api-dev.*.azurecontainerapps.io │
│      └── resourceGroupName: rg-doctors-api-dev                        │
└──────────────────────────────────────────────────────────────────────┘
```

### Estimated Cost (Dev)

| Resource | Idle | With Traffic |
|----------|------|-------------|
| Resource Group | $0 | $0 |
| Container App | $0 | $0–15 |
| SQL Serverless (auto-pause) | $0 | $0–3 |
| ACR Basic | $5 | $5 |
| Log Analytics | $2–5 | $2–5 |
| Key Vault | $3 | $3 |
| **Total** | **~$10/mes** | **~$15–20/mes** |

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

```bash
# src/Doctors.Api/Properties/launchSettings.json
# (auto-created by IDE, or create manually)
```

```json
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

# Set SQL password as encrypted secret
pulumi config set --secret sqlPassword
# → Enter a strong password: uppercase + lowercase + number + symbol, 8+ chars
```

**4. Purge old Key Vault (if exists from previous deployment)**

```bash
# Check if a soft-deleted Key Vault exists
az keyvault list-deleted --query "[?name=='kv-doctors-api-dev']" -o table

# If found, purge it
az keyvault purge --name kv-doctors-api-dev
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

# GET by ID (copy the id from POST response)
curl http://localhost:5000/api/doctors/{id}
```

### Deploy to Azure

**Full deploy (first time or after infra changes):**

```bash
# One command does everything:
.\deploy.ps1
```

Or step by step:

```bash
# 1. Create/update infrastructure
cd infra
pulumi up

# 2. Build Docker image
cd ..
docker build -t acrdoctorsapidev.azurecr.io/doctors-api:latest .

# 3. Push to ACR
az acr login --name acrdoctorsapidev
docker push acrdoctorsapidev.azurecr.io/doctors-api:latest

# 4. Update Container App (CRITICAL — without this, old image keeps running)
az containerapp update \
  --name ca-doctors-api-dev \
  --resource-group rg-doctors-api-dev \
  --image acrdoctorsapidev.azurecr.io/doctors-api:latest

# 5. Get the URL
az containerapp show \
  --name ca-doctors-api-dev \
  --resource-group rg-doctors-api-dev \
  --query "properties.configuration.ingress.fqdn" -o tsv
```

**Code-only redeploy (no infra changes):**

```bash
docker build -t acrdoctorsapidev.azurecr.io/doctors-api:latest .
az acr login --name acrdoctorsapidev
docker push acrdoctorsapidev.azurecr.io/doctors-api:latest
az containerapp update --name ca-doctors-api-dev --resource-group rg-doctors-api-dev --image acrdoctorsapidev.azurecr.io/doctors-api:latest
```

### Validate Deployment

```bash
$URL="https://ca-doctors-api-dev.politemushroom-27d105d9.westus2.azurecontainerapps.io"

# Health check
curl $URL/api/doctors

# Swagger UI
curl $URL/swagger

# ReDoc
curl $URL/redoc.html

# Create a doctor
curl -X POST "$URL/api/doctors" \
  -H "Content-Type: application/json" \
  -d '{"firstName":"María","lastName":"González","licenseNumber":"MP-67890","specialty":"Neurología"}'
```

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

### Flow of a POST Request

```
  Client
    │
    ▼
  ┌─────────────────────────────────────────────────────────┐
  │ DoctorEndpoints.CreateDoctorAsync()      [API layer]    │
  │   → ValidationFilter (DataAnnotations)                  │
  │   → service.CreateAsync(request)                        │
  └─────────────────────────┬───────────────────────────────┘
                            │
  ┌─────────────────────────▼───────────────────────────────┐
  │ DoctorService.CreateAsync()               [App layer]   │
  │   → repository.ExistsByLicenseNumberAsync()             │
  │   → request.ToEntity()                                  │
  │   → repository.AddAsync(doctor)                         │
  │   → doctor.ToDto()                                      │
  └─────────────────────────┬───────────────────────────────┘
                            │
  ┌─────────────────────────▼───────────────────────────────┐
  │ DoctorRepository.AddAsync()               [Infra layer] │
  │   → context.Doctors.Add(doctor)                         │
  │   → SaveChangesAsync()   → INSERT INTO Doctors          │
  └─────────────────────────────────────────────────────────┘
                            │
                            ▼
                      201 Created + DoctorDto
```

---

## Configuration

### Environment Variables (Container App)

| Variable | Value | Purpose |
|----------|-------|---------|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Enables Swagger/ReDoc |
| `ConnectionStrings__DefaultConnection` | `Server=...;Database=...;...` | Azure SQL connection |

> **Note:** Use double underscore (`__`) not colon (`:`) for nested config in environment variables.

### Pulumi Stack Config (infra/Pulumi.dev.yaml)

```yaml
config:
  doctors-api-infra:env: dev
  doctors-api-infra:sqlAdmin: doctorsadmin
  doctors-api-infra:tenantId: "<your-tenant-id>"
  doctors-api-infra:location: westus2
  doctors-api-infra:sqlPassword:
    secure: <encrypted>
```

---

## Known Issues

| Issue | Cause | Workaround |
|-------|-------|------------|
| **eastus2 SQL provisioning restricted** | Azure region limitation | Use `westus2` or `eastus` |
| **`Microsoft.AspNetCore.OpenApi` broken on .NET 10** | Pre-release package | Use NSwag.AspNetCore instead |
| **Swashbuckle 7.x incompatible with .NET 10** | `TypeLoadException` | Use NSwag.AspNetCore instead |
| **Docker build fails with Windows paths** | `obj/` contains `C:\...` paths | Use `**/obj/` (glob) in `.dockerignore` |
| **Key Vault name already taken** | Soft-delete retains names | `az keyvault purge --name <name>` |
| **Container App serves old image** | No auto-pull on push | Must call `az containerapp update` |
| **Swagger 404 on Azure** | Production mode disables it | Set `ASPNETCORE_ENVIRONMENT=Development` |
| **SQL firewall blocks Container App** | No Azure services rule | Add firewall rule `0.0.0.0`–`0.0.0.0` |
| **EF migrations fail outside DI** | No provider configured | Use `IDesignTimeDbContextFactory` |

---

## License

Internal project — demo/learning purposes.
