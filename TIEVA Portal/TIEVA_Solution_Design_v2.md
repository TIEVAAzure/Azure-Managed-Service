# TIEVA Portal - Complete Solution Design

## Executive Summary

TIEVA Portal is a secure, serverless Azure assessment management platform that enables automated infrastructure audits across customer Azure tenants. The solution uses Azure Functions for API and assessment execution, Azure Static Web Apps for the portal UI with built-in SSO, and Azure SQL for data persistence.

**Estimated Monthly Cost: £5-15**

---

## Architecture Overview

```
                                    ┌─────────────────────────────────────┐
                                    │         TIEVA Consultants           │
                                    │        (Entra ID Users)             │
                                    └─────────────────┬───────────────────┘
                                                      │ Login via SSO
                                                      ▼
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                           AZURE STATIC WEB APPS (Free Tier)                             │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐  │
│  │  TIEVA Portal (HTML/CSS/JS)                                                       │  │
│  │  ├── Dashboard (stats, alerts, recent activity)                                   │  │
│  │  ├── Customers (list, add, edit, detail view)                                     │  │
│  │  ├── Subscriptions (sync from Azure, assign tiers, set scope)                     │  │
│  │  ├── Assessments (run, view results, compare, trends)                             │  │
│  │  ├── Service Tiers (configure modules per tier)                                   │  │
│  │  ├── Connections (manage Service Principals)                                      │  │
│  │  ├── Assessment Hub (links to 8 standalone analyzers)                             │  │
│  │  └── Reports (export CSV, PDF summaries)                                          │  │
│  └───────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                         │
│  Built-in: Entra ID Authentication, CDN, SSL, Custom Domain Support                    │
└─────────────────────────────────────────────────────────────────────────────────────────┘
                                                      │
                                                      │ HTTPS API Calls
                                                      ▼
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                        AZURE FUNCTION APP (Consumption Plan)                            │
│                                                                                         │
│  ┌─────────────────────────────────────────────────────────────────────────────────┐    │
│  │  HTTP TRIGGER FUNCTIONS (C# / .NET 8)                                           │    │
│  │                                                                                 │    │
│  │  Customer Management          Subscription Management      Assessment Engine    │    │
│  │  ├── GET /customers          ├── GET /subscriptions       ├── POST /assess     │    │
│  │  ├── POST /customers         ├── POST /sync-subs          ├── GET /results     │    │
│  │  ├── PUT /customers/{id}     ├── PUT /sub/{id}/tier       ├── GET /trends      │    │
│  │  └── DELETE /customers/{id}  └── PUT /sub/{id}/scope      └── GET /compare     │    │
│  │                                                                                 │    │
│  │  Tier Management              Connection Management        Reporting            │    │
│  │  ├── GET /tiers              ├── GET /connections         ├── GET /export/csv  │    │
│  │  ├── PUT /tiers/{id}         ├── POST /connections        └── GET /export/pdf  │    │
│  │  └── PUT /tier-modules       ├── POST /validate-conn                           │    │
│  │                              └── DELETE /connections/{id}                       │    │
│  └─────────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                         │
│  ┌─────────────────────────────────────────────────────────────────────────────────┐    │
│  │  DURABLE FUNCTIONS (Long-running assessment orchestration)                      │    │
│  │                                                                                 │    │
│  │  AssessmentOrchestrator                                                         │    │
│  │  ├── Validates connection to customer tenant                                    │    │
│  │  ├── Determines modules to run based on subscription tier                       │    │
│  │  ├── Executes each module in parallel:                                          │    │
│  │  │   ├── NetworkAudit        ├── BackupAudit         ├── CostAudit             │    │
│  │  │   ├── IdentityAudit       ├── PolicyAudit         ├── ReservationAudit      │    │
│  │  │   ├── ResourceAudit       └── AdvisorAudit                                  │    │
│  │  ├── Aggregates results and calculates scores                                   │    │
│  │  ├── Compares with previous assessment (identifies new/resolved findings)       │    │
│  │  └── Stores results in database                                                 │    │
│  └─────────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                         │
│  Security: Managed Identity (no credentials in code)                                    │
└──────────────────────────────────┬─────────────────────────────────┬────────────────────┘
                                   │                                 │
                    ┌──────────────┴──────────────┐    ┌─────────────┴─────────────┐
                    ▼                             ▼    ▼                           ▼
┌─────────────────────────────────────┐  ┌─────────────────────────────────────────────────┐
│       AZURE SQL DATABASE            │  │              AZURE KEY VAULT                    │
│       (Basic Tier - £5/month)       │  │              (Free operations)                  │
│                                     │  │                                                 │
│  ┌───────────────────────────────┐  │  │  Stores securely:                               │
│  │  Customers                    │  │  │  ├── Customer Service Principal secrets        │
│  │  ├── Id, Name, Code           │  │  │  ├── SQL connection string                     │
│  │  ├── Industry, Contact, Email │  │  │  └── Any other sensitive config                │
│  │  └── CreatedAt, IsActive      │  │  │                                                 │
│  ├───────────────────────────────┤  │  │  Access: Only Function App (via Managed        │
│  │  AzureConnections             │  │  │          Identity) can read secrets            │
│  │  ├── CustomerId, TenantId     │  │  │                                                 │
│  │  ├── ClientId                 │  │  └─────────────────────────────────────────────────┘
│  │  ├── SecretKeyVaultRef ───────┼──┼──► Points to Key Vault secret
│  │  ├── SecretExpiry             │  │
│  │  └── LastValidated            │  │
│  ├───────────────────────────────┤  │
│  │  CustomerSubscriptions        │  │
│  │  ├── SubscriptionId, Name     │  │
│  │  ├── TierId (FK)              │  │
│  │  ├── Environment              │  │
│  │  ├── IsInScope                │  │
│  │  └── CustomerId, ConnectionId │  │
│  ├───────────────────────────────┤  │
│  │  ServiceTiers                 │  │
│  │  ├── Id, Name, DisplayName    │  │
│  │  ├── Color, Description       │  │
│  │  └── SortOrder, IsActive      │  │
│  ├───────────────────────────────┤  │
│  │  AssessmentModules            │  │
│  │  ├── Code, Name, Icon         │  │
│  │  ├── Category                 │  │
│  │  └── EstimatedMinutes         │  │
│  ├───────────────────────────────┤  │
│  │  TierModules                  │  │
│  │  ├── TierId, ModuleId         │  │
│  │  ├── IsIncluded               │  │
│  │  └── Frequency                │  │
│  ├───────────────────────────────┤  │
│  │  Assessments                  │  │
│  │  ├── CustomerId, ConnectionId │  │
│  │  ├── Status, StartedAt        │  │
│  │  ├── CompletedAt, StartedBy   │  │
│  │  ├── ScoreOverall             │  │
│  │  └── High/Med/Low Findings    │  │
│  ├───────────────────────────────┤  │
│  │  AssessmentModuleResults      │  │
│  │  ├── AssessmentId, ModuleCode │  │
│  │  ├── SubscriptionId           │  │
│  │  ├── Score, FindingCount      │  │
│  │  └── Status, Duration         │  │
│  ├───────────────────────────────┤  │
│  │  Findings                     │  │
│  │  ├── AssessmentId, Module     │  │
│  │  ├── Severity, Category       │  │
│  │  ├── Resource details         │  │
│  │  ├── Finding, Recommendation  │  │
│  │  ├── EffortHours, Owner       │  │
│  │  ├── Status, Hash             │  │
│  │  └── FirstSeen, ResolvedAt    │  │
│  └───────────────────────────────┘  │
└─────────────────────────────────────┘


                    CUSTOMER AZURE TENANTS
                    ══════════════════════
                    
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                         │
│   ┌─────────────────────┐   ┌─────────────────────┐   ┌─────────────────────┐          │
│   │  Customer A Tenant  │   │  Customer B Tenant  │   │  Customer C Tenant  │   ...    │
│   │                     │   │                     │   │                     │          │
│   │  Service Principal  │   │  Service Principal  │   │  Service Principal  │          │
│   │  (Reader role on    │   │  (Reader role on    │   │  (Reader role on    │          │
│   │   subscriptions)    │   │   subscriptions)    │   │   subscriptions)    │          │
│   │                     │   │                     │   │                     │          │
│   │  ┌───────────────┐  │   │  ┌───────────────┐  │   │  ┌───────────────┐  │          │
│   │  │ Subscription 1│  │   │  │ Subscription 1│  │   │  │ Subscription 1│  │          │
│   │  │ (Premium)     │  │   │  │ (Standard)    │  │   │  │ (Basic)       │  │          │
│   │  ├───────────────┤  │   │  ├───────────────┤  │   │  └───────────────┘  │          │
│   │  │ Subscription 2│  │   │  │ Subscription 2│  │   │                     │          │
│   │  │ (Standard)    │  │   │  │ (Standard)    │  │   │                     │          │
│   │  ├───────────────┤  │   │  └───────────────┘  │   │                     │          │
│   │  │ Subscription 3│  │   │                     │   │                     │          │
│   │  │ (Out of Scope)│  │   │                     │   │                     │          │
│   │  └───────────────┘  │   │                     │   │                     │          │
│   └─────────────────────┘   └─────────────────────┘   └─────────────────────┘          │
│                                                                                         │
│   Assessment Flow:                                                                      │
│   1. Function App authenticates using stored Service Principal credentials             │
│   2. Queries Azure Resource Manager APIs (read-only)                                   │
│   3. Collects: Resources, Policies, RBAC, Backup, Advisor, Network, Cost data         │
│   4. Returns data to Function App for processing and storage                           │
│                                                                                         │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Security Architecture

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                              SECURITY LAYERS                                            │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                         │
│  LAYER 1: User Authentication                                                           │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Azure Static Web Apps + Entra ID                                                 │  │
│  │  • Users must authenticate with your Entra ID tenant                              │  │
│  │  • Only users in your organization can access the portal                          │  │
│  │  • Supports MFA if enabled on your tenant                                         │  │
│  │  • Role-based access possible (Admin, Viewer, etc.)                               │  │
│  └───────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                         │
│  LAYER 2: API Security                                                                  │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Azure Functions                                                                  │  │
│  │  • HTTPS only (TLS 1.2+)                                                          │  │
│  │  • Validates user token from Static Web Apps                                      │  │
│  │  • CORS restricted to Static Web Apps domain only                                 │  │
│  │  • No credentials stored in code or config                                        │  │
│  └───────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                         │
│  LAYER 3: Secrets Management                                                            │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Azure Key Vault                                                                  │  │
│  │  • Customer Service Principal secrets stored encrypted                            │  │
│  │  • SQL connection string stored as secret                                         │  │
│  │  • Only Function App can access (via Managed Identity)                            │  │
│  │  • Audit logging enabled                                                          │  │
│  │  • Secret expiry tracking                                                         │  │
│  └───────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                         │
│  LAYER 4: Data Security                                                                 │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Azure SQL Database                                                               │  │
│  │  • Encrypted at rest (TDE)                                                        │  │
│  │  • Encrypted in transit (TLS)                                                     │  │
│  │  • Firewall: Only Azure services can connect                                      │  │
│  │  • No public endpoint (optional: Private Link)                                    │  │
│  │  • Managed Identity authentication (no SQL password in code)                      │  │
│  └───────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                         │
│  LAYER 5: Customer Tenant Access                                                        │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Service Principal (per customer)                                                 │  │
│  │  • Minimum permissions: Reader role only                                          │  │
│  │  • Scoped to specific subscriptions                                               │  │
│  │  • Customer creates and controls the SP                                           │  │
│  │  • Secret rotation tracked and alerted                                            │  │
│  │  • Read-only access - cannot modify customer resources                            │  │
│  └───────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                         │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Data Flow Diagrams

### Flow 1: User Login
```
┌──────────┐     ┌─────────────────┐     ┌───────────────┐     ┌──────────────┐
│  User    │────►│ Static Web Apps │────►│   Entra ID    │────►│ User's       │
│          │     │ (portal URL)    │     │ Login Page    │     │ Credentials  │
└──────────┘     └─────────────────┘     └───────────────┘     └──────────────┘
                                                   │
                         ┌─────────────────────────┘
                         ▼
                 ┌───────────────┐     ┌─────────────────┐
                 │ Token issued  │────►│ User redirected │
                 │ (JWT)         │     │ to portal       │
                 └───────────────┘     └─────────────────┘
```

### Flow 2: Add Customer Connection
```
┌──────────┐     ┌─────────────────┐     ┌───────────────┐
│  User    │────►│ Portal: Add     │────►│ Function API  │
│          │     │ Connection Form │     │ /connections  │
└──────────┘     └─────────────────┘     └───────────────┘
                                                 │
                                                 ▼
                                         ┌───────────────┐
                                         │ Validate with │
                                         │ Azure (test   │
                                         │ SP creds)     │
                                         └───────┬───────┘
                                                 │
                         ┌───────────────────────┼───────────────────────┐
                         ▼                       ▼                       ▼
                 ┌───────────────┐       ┌───────────────┐       ┌───────────────┐
                 │ Store secret  │       │ Store metadata│       │ Return list   │
                 │ in Key Vault  │       │ in SQL DB     │       │ of accessible │
                 └───────────────┘       └───────────────┘       │ subscriptions │
                                                                 └───────────────┘
```

### Flow 3: Run Assessment
```
┌──────────┐     ┌─────────────────┐     ┌───────────────┐
│  User    │────►│ Select Customer │────►│ Click "Run    │
│          │     │ & Subscriptions │     │ Assessment"   │
└──────────┘     └─────────────────┘     └───────────────┘
                                                 │
                                                 ▼
                                         ┌───────────────────────────────────┐
                                         │      DURABLE FUNCTION             │
                                         │      (Orchestrator)               │
                                         └───────────────┬───────────────────┘
                                                         │
                 ┌───────────────────────────────────────┼───────────────────────────────────────┐
                 │                                       │                                       │
                 ▼                                       ▼                                       ▼
         ┌───────────────┐                       ┌───────────────┐                       ┌───────────────┐
         │ Get SP secret │                       │ Check tier    │                       │ Create assess │
         │ from KeyVault │                       │ for modules   │                       │ record in DB  │
         └───────┬───────┘                       └───────┬───────┘                       └───────────────┘
                 │                                       │
                 ▼                                       ▼
         ┌───────────────┐               ┌───────────────────────────────────┐
         │ Authenticate  │               │     RUN MODULES IN PARALLEL       │
         │ to customer   │               ├───────────────────────────────────┤
         │ Azure tenant  │               │ ┌─────────┐ ┌─────────┐ ┌───────┐ │
         └───────────────┘               │ │Network  │ │Identity │ │Backup │ │
                                         │ └─────────┘ └─────────┘ └───────┘ │
                                         │ ┌─────────┐ ┌─────────┐ ┌───────┐ │
                                         │ │Cost     │ │Policy   │ │Reserve│ │
                                         │ └─────────┘ └─────────┘ └───────┘ │
                                         │ ┌─────────┐ ┌─────────┐           │
                                         │ │Resource │ │Advisor  │           │
                                         │ └─────────┘ └─────────┘           │
                                         └───────────────┬───────────────────┘
                                                         │
                                                         ▼
                                         ┌───────────────────────────────────┐
                                         │   AGGREGATE & SCORE               │
                                         │   • Calculate module scores       │
                                         │   • Calculate overall score       │
                                         │   • Identify new findings         │
                                         │   • Identify resolved findings    │
                                         │   • Compare with previous run     │
                                         └───────────────┬───────────────────┘
                                                         │
                                                         ▼
                                         ┌───────────────────────────────────┐
                                         │   STORE RESULTS                   │
                                         │   • Assessment record             │
                                         │   • Module results                │
                                         │   • All findings                  │
                                         │   • Update finding lifecycle      │
                                         └───────────────────────────────────┘
                                                         │
                                                         ▼
                                         ┌───────────────────────────────────┐
                                         │   RETURN TO PORTAL                │
                                         │   • Show results dashboard        │
                                         │   • Display score comparison      │
                                         │   • List new/resolved findings    │
                                         └───────────────────────────────────┘
```

---

## Project Phases

### Phase 1: Foundation (Week 1)
**Goal: Core infrastructure and basic portal working**

| Task | Description | Deliverable |
|------|-------------|-------------|
| 1.1 | Create Azure resources | Resource Group, SQL, Key Vault, Function App, Static Web App |
| 1.2 | Set up database schema | All tables created with seed data (tiers, modules) |
| 1.3 | Deploy basic Function API | CRUD endpoints for customers, tiers, modules |
| 1.4 | Deploy portal HTML | Static Web App with Entra ID SSO |
| 1.5 | Connect portal to API | Dashboard shows live data |

**End of Phase 1:** Portal accessible via SSO, can view/add customers, view tier configuration.

---

### Phase 2: Connection Management (Week 2)
**Goal: Manage customer Azure connections securely**

| Task | Description | Deliverable |
|------|-------------|-------------|
| 2.1 | Add connection endpoints | POST/GET/DELETE for connections |
| 2.2 | Key Vault integration | Secrets stored securely |
| 2.3 | Connection validation | Test SP can authenticate |
| 2.4 | Subscription sync | Discover all subs SP can access |
| 2.5 | Subscription management UI | Assign tiers, set scope, environment |
| 2.6 | Customer onboarding script | PowerShell script customer runs |

**End of Phase 2:** Can onboard customers, sync their subscriptions, assign tiers.

---

### Phase 3: Assessment Engine (Week 3-4)
**Goal: Automated assessment execution**

| Task | Description | Deliverable |
|------|-------------|-------------|
| 3.1 | Durable Function orchestrator | Long-running assessment workflow |
| 3.2 | Network audit module | VNets, NSGs, peerings, gateways |
| 3.3 | Identity audit module | RBAC, SPs, guests, PIM |
| 3.4 | Backup audit module | RSV, coverage, policies |
| 3.5 | Cost audit module | Budgets, alerts, tags |
| 3.6 | Policy audit module | Assignments, compliance |
| 3.7 | Reservation audit module | RI utilization, savings |
| 3.8 | Resource audit module | Inventory, utilization |
| 3.9 | Advisor audit module | Recommendations |
| 3.10 | Scoring engine | Calculate scores per module and overall |
| 3.11 | Assessment UI | Run, monitor progress, view results |

**End of Phase 3:** Can run full automated assessment against customer Azure.

---

### Phase 4: Tracking & Comparison (Week 5)
**Goal: Historical tracking and trend analysis**

| Task | Description | Deliverable |
|------|-------------|-------------|
| 4.1 | Finding lifecycle | Track new/open/resolved/recurring |
| 4.2 | Assessment comparison | Diff view between two assessments |
| 4.3 | Trend charts | Score over time, findings over time |
| 4.4 | Customer dashboard | Score, modules, history at a glance |
| 4.5 | Alerts | Expiring secrets, score drops, critical findings |

**End of Phase 4:** Full historical tracking, trends, comparison views.

---

### Phase 5: Reporting & Polish (Week 6)
**Goal: Professional outputs and refinements**

| Task | Description | Deliverable |
|------|-------------|-------------|
| 5.1 | CSV export | Findings formatted for Customer_Review.xlsx |
| 5.2 | Executive summary PDF | One-page score summary |
| 5.3 | Remediation roadmap | Wave-based implementation plan |
| 5.4 | Email notifications | Assessment complete, alerts |
| 5.5 | Standalone analyzers | Integrate existing HTML tools |
| 5.6 | Documentation | User guide, admin guide |

**End of Phase 5:** Production-ready solution.

---

## Azure Resource Summary

| Resource | SKU | Estimated Cost | Purpose |
|----------|-----|----------------|---------|
| Resource Group | - | £0 | Container for all resources |
| Azure SQL Database | Basic (2GB) | £5/month | Data storage |
| Azure Key Vault | Standard | £0 (first 10k ops free) | Secret storage |
| Azure Function App | Consumption | £0-5/month | API + Assessment engine |
| Azure Static Web Apps | Free | £0 | Portal hosting + SSO |
| Application Insights | Free tier | £0 | Monitoring |
| **TOTAL** | | **£5-10/month** | |

---

## File Structure

```
TIEVA/
├── infrastructure/
│   ├── deploy.ps1                 # One-click deployment script
│   ├── main.bicep                 # Infrastructure as code
│   └── parameters.json            # Environment parameters
│
├── database/
│   └── schema.sql                 # Full database schema + seed data
│
├── functions/
│   ├── TIEVA.Functions.sln
│   ├── src/
│   │   ├── TIEVA.Functions/
│   │   │   ├── Program.cs
│   │   │   ├── Startup.cs
│   │   │   ├── Functions/
│   │   │   │   ├── CustomerFunctions.cs
│   │   │   │   ├── TierFunctions.cs
│   │   │   │   ├── ConnectionFunctions.cs
│   │   │   │   ├── SubscriptionFunctions.cs
│   │   │   │   ├── AssessmentFunctions.cs
│   │   │   │   └── ReportFunctions.cs
│   │   │   ├── Orchestrators/
│   │   │   │   └── AssessmentOrchestrator.cs
│   │   │   ├── Activities/
│   │   │   │   ├── NetworkAuditActivity.cs
│   │   │   │   ├── IdentityAuditActivity.cs
│   │   │   │   ├── BackupAuditActivity.cs
│   │   │   │   ├── CostAuditActivity.cs
│   │   │   │   ├── PolicyAuditActivity.cs
│   │   │   │   ├── ReservationAuditActivity.cs
│   │   │   │   ├── ResourceAuditActivity.cs
│   │   │   │   └── AdvisorAuditActivity.cs
│   │   │   ├── Models/
│   │   │   └── Services/
│   │   └── TIEVA.Functions.Tests/
│   └── local.settings.json
│
├── portal/
│   ├── index.html                 # Main portal (dashboard)
│   ├── customers.html             # Customer management
│   ├── subscriptions.html         # Subscription management
│   ├── assessments.html           # Assessment list & results
│   ├── tiers.html                 # Tier configuration
│   ├── connections.html           # Connection management
│   ├── hub.html                   # Links to analyzers
│   ├── css/
│   │   └── styles.css
│   ├── js/
│   │   ├── api.js                 # API client
│   │   ├── auth.js                # SSO handling
│   │   └── app.js                 # Main application logic
│   ├── analyzers/                 # Your existing HTML analyzers
│   │   ├── TIEVA_Advisor_Effort_Estimator.html
│   │   ├── TIEVA_Backup_Posture_Analyzer.html
│   │   ├── TIEVA_Cost_Management_Analyzer.html
│   │   ├── TIEVA_Network_Topology_Analyzer.html
│   │   ├── TIEVA_Identity_Access_Analyzer.html
│   │   ├── TIEVA_Policy_Compliance_Analyzer.html
│   │   ├── TIEVA_Resource_Analyzer.html
│   │   └── TIEVA_Reservation_Savings_Analyzer.html
│   └── staticwebapp.config.json   # Auth configuration
│
├── scripts/
│   └── TIEVA_Customer_Onboarding.ps1
│
└── docs/
    ├── README.md
    ├── USER_GUIDE.md
    └── ADMIN_GUIDE.md
```

---

## Next Steps

1. **Confirm this plan** - Does this cover everything you need?
2. **Delete existing Azure resources** - Clean slate
3. **Start Phase 1** - I'll provide step-by-step commands

Ready to proceed?
