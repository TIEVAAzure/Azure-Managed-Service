CREATE USER [func-tievaPortal-6612] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-tievaPortal-6612];
ALTER ROLE db_datawriter ADD MEMBER [func-tievaPortal-6612];

-- Service Tiers
CREATE TABLE ServiceTiers (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(50) NOT NULL,
    DisplayName NVARCHAR(100) NOT NULL,
    Description NVARCHAR(500),
    Color NVARCHAR(20),
    SortOrder INT DEFAULT 0,
    IsActive BIT DEFAULT 1,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Assessment Modules
CREATE TABLE AssessmentModules (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Code NVARCHAR(50) NOT NULL UNIQUE,
    Name NVARCHAR(100) NOT NULL,
    Description NVARCHAR(500),
    Icon NVARCHAR(10),
    Category NVARCHAR(50),
    EstimatedMinutes INT DEFAULT 5,
    SortOrder INT DEFAULT 0,
    IsActive BIT DEFAULT 1
);

-- Tier Module Mapping
CREATE TABLE TierModules (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    TierId UNIQUEIDENTIFIER NOT NULL REFERENCES ServiceTiers(Id),
    ModuleId UNIQUEIDENTIFIER NOT NULL REFERENCES AssessmentModules(Id),
    IsIncluded BIT DEFAULT 1,
    Frequency NVARCHAR(20) DEFAULT 'Monthly',
    UNIQUE(TierId, ModuleId)
);

-- Customers
CREATE TABLE Customers (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(200) NOT NULL,
    Code NVARCHAR(50),
    Industry NVARCHAR(100),
    PrimaryContact NVARCHAR(200),
    Email NVARCHAR(200),
    Phone NVARCHAR(50),
    Notes NVARCHAR(MAX),
    IsActive BIT DEFAULT 1,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Azure Connections
CREATE TABLE AzureConnections (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    CustomerId UNIQUEIDENTIFIER NOT NULL REFERENCES Customers(Id),
    TenantId NVARCHAR(100) NOT NULL,
    TenantName NVARCHAR(200),
    ClientId NVARCHAR(100) NOT NULL,
    SecretKeyVaultRef NVARCHAR(200) NOT NULL,
    SecretExpiry DATETIME2,
    IsActive BIT DEFAULT 1,
    LastValidated DATETIME2,
    LastValidationStatus NVARCHAR(50),
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Customer Subscriptions
CREATE TABLE CustomerSubscriptions (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    ConnectionId UNIQUEIDENTIFIER NOT NULL REFERENCES AzureConnections(Id),
    SubscriptionId NVARCHAR(100) NOT NULL,
    SubscriptionName NVARCHAR(200),
    TierId UNIQUEIDENTIFIER REFERENCES ServiceTiers(Id),
    Environment NVARCHAR(50) DEFAULT 'Production',
    IsInScope BIT DEFAULT 1,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
    UNIQUE(ConnectionId, SubscriptionId)
);

-- Assessments
CREATE TABLE Assessments (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    CustomerId UNIQUEIDENTIFIER NOT NULL REFERENCES Customers(Id),
    ConnectionId UNIQUEIDENTIFIER REFERENCES AzureConnections(Id),
    Status NVARCHAR(50) DEFAULT 'Pending',
    StartedAt DATETIME2,
    CompletedAt DATETIME2,
    StartedBy NVARCHAR(200),
    ScoreOverall DECIMAL(5,2),
    FindingsTotal INT DEFAULT 0,
    FindingsHigh INT DEFAULT 0,
    FindingsMedium INT DEFAULT 0,
    FindingsLow INT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Assessment Module Results
CREATE TABLE AssessmentModuleResults (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    AssessmentId UNIQUEIDENTIFIER NOT NULL REFERENCES Assessments(Id),
    ModuleCode NVARCHAR(50) NOT NULL,
    SubscriptionId NVARCHAR(100),
    Status NVARCHAR(50) DEFAULT 'Pending',
    Score DECIMAL(5,2),
    FindingsCount INT DEFAULT 0,
    StartedAt DATETIME2,
    CompletedAt DATETIME2,
    DurationSeconds INT,
    ErrorMessage NVARCHAR(MAX)
);

-- Findings
CREATE TABLE Findings (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    AssessmentId UNIQUEIDENTIFIER NOT NULL REFERENCES Assessments(Id),
    ModuleCode NVARCHAR(50) NOT NULL,
    SubscriptionId NVARCHAR(100),
    Severity NVARCHAR(20) NOT NULL,
    Category NVARCHAR(100),
    ResourceType NVARCHAR(100),
    ResourceName NVARCHAR(200),
    ResourceId NVARCHAR(500),
    Finding NVARCHAR(MAX) NOT NULL,
    Recommendation NVARCHAR(MAX),
    EffortHours DECIMAL(5,2),
    Owner NVARCHAR(100),
    Status NVARCHAR(50) DEFAULT 'Open',
    Hash NVARCHAR(64),
    FirstSeenAt DATETIME2 DEFAULT GETUTCDATE(),
    ResolvedAt DATETIME2
);

-- Indexes
CREATE INDEX IX_Assessments_CustomerId ON Assessments(CustomerId);
CREATE INDEX IX_Findings_AssessmentId ON Findings(AssessmentId);
CREATE INDEX IX_Findings_Severity ON Findings(Severity);
CREATE INDEX IX_CustomerSubscriptions_ConnectionId ON CustomerSubscriptions(ConnectionId);

-- Insert Service Tiers
INSERT INTO ServiceTiers (Name, DisplayName, Description, Color, SortOrder) VALUES
('Premium', 'Premium', 'Full monthly assessments with all modules', '#8b5cf6', 1),
('Standard', 'Standard', 'Core monthly assessments', '#3b82f6', 2),
('Basic', 'Basic', 'Quarterly essential assessments', '#6b7280', 3),
('AdHoc', 'Ad-hoc', 'On-demand assessments', '#f59e0b', 4);

-- Insert Assessment Modules
INSERT INTO AssessmentModules (Code, Name, Description, Icon, Category, EstimatedMinutes, SortOrder) VALUES
('NETWORK', 'Network Topology', 'VNets, subnets, NSGs, peerings, gateways', N'🌐', 'Security', 10, 1),
('IDENTITY', 'Identity & Access', 'RBAC, service principals, guests, PIM', N'👤', 'Security', 8, 2),
('BACKUP', 'Backup Posture', 'Recovery Services, backup coverage, policies', N'💾', 'Operations', 6, 3),
('COST', 'Cost Management', 'Budgets, alerts, anomalies, tags', N'💰', 'Cost', 5, 4),
('POLICY', 'Policy & Compliance', 'Policy assignments, compliance state', N'📋', 'Governance', 7, 5),
('RESERVATION', 'Reservations & Savings', 'RI utilization, savings plans', N'💵', 'Cost', 5, 6),
('RESOURCE', 'Resource Inventory', 'Full resource inventory with utilization', N'📦', 'Operations', 10, 7),
('ADVISOR', 'Advisor Recommendations', 'Azure Advisor findings', N'📊', 'Operations', 4, 8);

-- Map modules to tiers
DECLARE @PremiumId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE Name = 'Premium');
DECLARE @StandardId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE Name = 'Standard');
DECLARE @BasicId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE Name = 'Basic');
DECLARE @AdHocId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE Name = 'AdHoc');

-- Premium: All modules, Monthly
INSERT INTO TierModules (TierId, ModuleId, Frequency)
SELECT @PremiumId, Id, 'Monthly' FROM AssessmentModules;

-- Standard: Core modules, Monthly
INSERT INTO TierModules (TierId, ModuleId, Frequency)
SELECT @StandardId, Id, 'Monthly' FROM AssessmentModules 
WHERE Code IN ('NETWORK', 'IDENTITY', 'BACKUP', 'COST', 'RESOURCE', 'ADVISOR');

-- Basic: Essential modules, Quarterly
INSERT INTO TierModules (TierId, ModuleId, Frequency)
SELECT @BasicId, Id, 'Quarterly' FROM AssessmentModules 
WHERE Code IN ('NETWORK', 'IDENTITY', 'BACKUP');

-- Ad-hoc: All modules available, OnDemand
INSERT INTO TierModules (TierId, ModuleId, Frequency)
SELECT @AdHocId, Id, 'OnDemand' FROM AssessmentModules;