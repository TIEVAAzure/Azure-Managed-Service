# TIEVA Portal — Development Deployment Guide (Part 2)

## Step 4: Configure and Run the API

### Update Connection String

Edit `src/TIEVA.Api/appsettings.json` with your actual values:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:sql-tieva-dev-XXXX.database.windows.net,1433;Initial Catalog=sqldb-tieva;Persist Security Info=False;User ID=tievaadmin;Password=YOUR-PASSWORD;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
  },
  "Azure": {
    "KeyVaultUri": "https://kv-tieva-dev-XXXX.vault.azure.net/"
  }
}
```

### Create the Database Schema

```powershell
# Using SQL Server Management Studio:
# 1. Connect to your Azure SQL Server
# 2. Open scripts/001_InitialSchema.sql
# 3. Execute against sqldb-tieva database

# OR using sqlcmd:
sqlcmd -S sql-tieva-dev-XXXX.database.windows.net -d sqldb-tieva -U tievaadmin -P "YOUR-PASSWORD" -i scripts/001_InitialSchema.sql
```

### Run the API Locally

```powershell
cd C:\Projects\TIEVA-Portal\src\TIEVA.Api

# Restore packages
dotnet restore

# Run in development mode
dotnet run

# API will be available at:
# - http://localhost:5000
# - https://localhost:5001
# - Swagger UI: https://localhost:5001/swagger
```

### Test the API

```powershell
# Health check
curl http://localhost:5000/health

# Get service tiers
curl http://localhost:5000/api/tiers

# Get modules
curl http://localhost:5000/api/modules

# Create a customer
curl -X POST http://localhost:5000/api/customers `
  -H "Content-Type: application/json" `
  -d '{"name":"Contoso Ltd","code":"CONTOSO","industry":"Manufacturing"}'

# Get dashboard stats
curl http://localhost:5000/api/dashboard
```

---

## Step 5: Run the Frontend (Optional - Use Prototype for Now)

For initial testing, you can use the static HTML prototype. For a full React frontend:

```powershell
cd C:\Projects\TIEVA-Portal\src\TIEVA.Web

# Create Next.js app
npx create-next-app@latest . --typescript --tailwind --eslint

# Install additional dependencies
npm install @tanstack/react-query axios lucide-react

# Run development server
npm run dev

# Frontend at http://localhost:3000
```

---

## Step 6: Test End-to-End Flow

### 1. Create a Customer

```http
POST http://localhost:5000/api/customers
Content-Type: application/json

{
  "name": "Contoso Ltd",
  "code": "CONTOSO",
  "industry": "Manufacturing",
  "primaryContact": "John Smith",
  "email": "john.smith@contoso.com"
}
```

### 2. Add a Connection

```http
POST http://localhost:5000/api/customers/{customerId}/connections
Content-Type: application/json

{
  "displayName": "Production",
  "tenantId": "your-tenant-id",
  "tenantName": "contoso.onmicrosoft.com",
  "clientId": "your-client-id",
  "clientSecret": "your-client-secret",
  "secretExpiryDate": "2025-12-15T00:00:00Z"
}
```

### 3. Sync Subscriptions

```http
POST http://localhost:5000/api/customers/{customerId}/subscriptions/sync
```

### 4. Assign Tiers to Subscriptions

```http
PUT http://localhost:5000/api/subscriptions/{subscriptionId}
Content-Type: application/json

{
  "tierId": "11111111-1111-1111-1111-111111111111",
  "environment": "Production",
  "isInScope": true
}
```

### 5. Start an Assessment

```http
POST http://localhost:5000/api/assessments
Content-Type: application/json

{
  "customerId": "{customerId}",
  "connectionId": "{connectionId}"
}
```

---

## Directory Structure

After setup, your project should look like:

```
C:\Projects\TIEVA-Portal\
├── TIEVA.sln
├── docs\
│   └── (documentation files)
├── scripts\
│   └── 001_InitialSchema.sql
└── src\
    ├── TIEVA.Api\
    │   ├── Program.cs
    │   ├── appsettings.json
    │   └── TIEVA.Api.csproj
    ├── TIEVA.Core\
    │   ├── Models\
    │   │   └── Entities.cs
    │   └── TIEVA.Core.csproj
    ├── TIEVA.Infrastructure\
    │   ├── Data\
    │   │   └── TievaDbContext.cs
    │   └── TIEVA.Infrastructure.csproj
    └── TIEVA.Web\
        └── (React/Next.js frontend)
```

---

## Next Steps

### Phase 1 Complete ✅
- [x] Database schema created
- [x] API endpoints working
- [x] CRUD for customers, connections, subscriptions
- [x] Service tier configuration

### Phase 2: Azure Integration
- [ ] Key Vault integration for secrets
- [ ] Actual Azure connection validation
- [ ] Subscription discovery from Azure
- [ ] Basic assessment execution

### Phase 3: Assessment Engine
- [ ] Azure Functions for assessment
- [ ] Port PowerShell scripts to .NET
- [ ] Real-time progress tracking
- [ ] Finding generation and storage

---

## Troubleshooting

### SQL Connection Issues

```powershell
# Test SQL connectivity
Test-NetConnection -ComputerName sql-tieva-dev-XXXX.database.windows.net -Port 1433

# Check firewall rules
az sql server firewall-rule list --resource-group rg-tieva-dev --server sql-tieva-dev-XXXX
```

### API Won't Start

```powershell
# Check for port conflicts
netstat -ano | findstr :5000

# Run with detailed logging
dotnet run --verbosity detailed
```

### Database Migration

If you need to recreate the database:

```powershell
# Drop and recreate (DEV ONLY!)
az sql db delete --resource-group rg-tieva-dev --server sql-tieva-dev-XXXX --name sqldb-tieva --yes
az sql db create --resource-group rg-tieva-dev --server sql-tieva-dev-XXXX --name sqldb-tieva --service-objective Basic

# Re-run schema script
sqlcmd -S sql-tieva-dev-XXXX.database.windows.net -d sqldb-tieva -U tievaadmin -P "PASSWORD" -i scripts/001_InitialSchema.sql
```

---

## Quick Reference

| Resource | Value |
|----------|-------|
| Resource Group | `rg-tieva-dev` |
| SQL Server | `sql-tieva-dev-XXXX.database.windows.net` |
| Database | `sqldb-tieva` |
| Key Vault | `kv-tieva-dev-XXXX` |
| API (local) | `http://localhost:5000` |
| Swagger | `http://localhost:5000/swagger` |
| Frontend (local) | `http://localhost:3000` |
