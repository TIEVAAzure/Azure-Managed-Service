-- ============================================================================
-- TIEVA Portal - Team Members Migration
-- Date: January 2025
-- Description: Adds TeamMembers table and TeamLeadId to Customers
-- ============================================================================

-- ============================================================================
-- 1. Create TeamMembers table
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TeamMembers')
BEGIN
    CREATE TABLE TeamMembers (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        Name NVARCHAR(255) NOT NULL,
        Email NVARCHAR(255) NOT NULL,
        AzureAdObjectId NVARCHAR(255) NULL,
        Role NVARCHAR(100) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    -- Unique email constraint
    CREATE UNIQUE INDEX IX_TeamMembers_Email ON TeamMembers(Email);

    -- Azure AD Object ID index for future integration
    CREATE INDEX IX_TeamMembers_AzureAdObjectId ON TeamMembers(AzureAdObjectId) WHERE AzureAdObjectId IS NOT NULL;

    -- Active filter index
    CREATE INDEX IX_TeamMembers_IsActive ON TeamMembers(IsActive);

    PRINT 'Created TeamMembers table with indexes';
END
ELSE
BEGIN
    PRINT 'TeamMembers table already exists';
END
GO

-- ============================================================================
-- 2. Add TeamLeadId to Customers table
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('Customers') AND name = 'TeamLeadId'
)
BEGIN
    ALTER TABLE Customers
    ADD TeamLeadId UNIQUEIDENTIFIER NULL;

    -- Add foreign key constraint
    ALTER TABLE Customers
    ADD CONSTRAINT FK_Customers_TeamLead
    FOREIGN KEY (TeamLeadId) REFERENCES TeamMembers(Id);

    -- Add index for lookups
    CREATE INDEX IX_Customers_TeamLeadId ON Customers(TeamLeadId) WHERE TeamLeadId IS NOT NULL;

    PRINT 'Added TeamLeadId column to Customers table with foreign key';
END
ELSE
BEGIN
    PRINT 'TeamLeadId column already exists';
END
GO
