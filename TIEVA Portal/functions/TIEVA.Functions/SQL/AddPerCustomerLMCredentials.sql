-- ============================================================================
-- TIEVA Portal - Per-Customer LogicMonitor Credentials Migration
-- Date: January 2026
-- Description: Adds tracking for per-customer LM credentials stored in Key Vault
-- ============================================================================

-- Security Note: Actual credentials are stored in Azure Key Vault as:
--   LM-{CustomerId}-Company
--   LM-{CustomerId}-AccessId
--   LM-{CustomerId}-AccessKey
-- Only the existence flag is stored in the database.

-- ============================================================================
-- 1. Add LMHasCustomCredentials to Customers table
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns 
    WHERE object_id = OBJECT_ID('Customers') AND name = 'LMHasCustomCredentials'
)
BEGIN
    ALTER TABLE Customers 
    ADD LMHasCustomCredentials BIT NOT NULL DEFAULT 0;
    
    PRINT 'Added LMHasCustomCredentials column to Customers table';
END
ELSE
BEGIN
    PRINT 'LMHasCustomCredentials column already exists';
END
GO

-- ============================================================================
-- 2. Verify existing columns
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns 
    WHERE object_id = OBJECT_ID('Customers') AND name = 'LogicMonitorGroupId'
)
BEGIN
    ALTER TABLE Customers 
    ADD LogicMonitorGroupId INT NULL;
    
    PRINT 'Added LogicMonitorGroupId column to Customers table';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns 
    WHERE object_id = OBJECT_ID('Customers') AND name = 'LMEnabled'
)
BEGIN
    ALTER TABLE Customers 
    ADD LMEnabled BIT NOT NULL DEFAULT 0;
    
    PRINT 'Added LMEnabled column to Customers table';
END
GO

-- ============================================================================
-- 3. Create index for efficient queries
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes 
    WHERE object_id = OBJECT_ID('Customers') AND name = 'IX_Customers_LMEnabled'
)
BEGIN
    CREATE INDEX IX_Customers_LMEnabled 
    ON Customers(LMEnabled, LMHasCustomCredentials) 
    WHERE LMEnabled = 1;
    
    PRINT 'Created index IX_Customers_LMEnabled';
END
GO

-- ============================================================================
-- 4. Verify AlertCount column exists in LMSyncStatuses
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns 
    WHERE object_id = OBJECT_ID('LMSyncStatuses') AND name = 'AlertCount'
)
BEGIN
    ALTER TABLE LMSyncStatuses 
    ADD AlertCount INT NULL;
    
    PRINT 'Added AlertCount column to LMSyncStatuses table';
END
GO

-- ============================================================================
-- 5. View current state
-- ============================================================================
SELECT 
    Id,
    Name,
    LMEnabled,
    LogicMonitorGroupId,
    LMHasCustomCredentials,
    CASE 
        WHEN LMHasCustomCredentials = 1 THEN 'Customer Portal'
        WHEN LogicMonitorGroupId IS NOT NULL THEN 'TIEVA Shared Portal'
        ELSE 'Not Configured'
    END AS LMConfigType
FROM Customers
WHERE IsActive = 1
ORDER BY Name;
GO

PRINT 'Per-customer LogicMonitor credentials migration complete!';
GO
