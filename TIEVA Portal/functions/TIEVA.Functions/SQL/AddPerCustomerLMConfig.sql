-- ============================================================================
-- TIEVA Portal - Per-Customer LogicMonitor Configuration
-- Date: January 2025
-- Description: Adds LMEnabled flag for per-customer LM credential support
-- 
-- Design: Credentials stored in Key Vault with naming convention:
--         LM-{CustomerId}-Company, LM-{CustomerId}-AccessId, LM-{CustomerId}-AccessKey
--         Customer ID itself acts as the Key Vault prefix (guaranteed unique)
-- ============================================================================

-- ============================================================================
-- 1. Add LMEnabled to Customers table
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns 
    WHERE object_id = OBJECT_ID('Customers') AND name = 'LMEnabled'
)
BEGIN
    ALTER TABLE Customers 
    ADD LMEnabled BIT NOT NULL DEFAULT 0;
    
    PRINT 'Added LMEnabled column to Customers table';
END
ELSE
BEGIN
    PRINT 'LMEnabled column already exists';
END
GO

-- ============================================================================
-- 2. Auto-enable LM for customers that already have LogicMonitorGroupId set
--    (These customers use TIEVA's global LM credentials)
-- ============================================================================
UPDATE Customers 
SET LMEnabled = 1 
WHERE LogicMonitorGroupId IS NOT NULL 
  AND LMEnabled = 0;

PRINT 'Enabled LM for customers with existing LogicMonitorGroupId mappings';
GO

-- ============================================================================
-- 3. Verify configuration
-- ============================================================================
SELECT 
    Id,
    Name,
    LogicMonitorGroupId,
    LMEnabled,
    CASE 
        WHEN LMEnabled = 1 AND LogicMonitorGroupId IS NOT NULL THEN 'Using Global Credentials'
        WHEN LMEnabled = 1 AND LogicMonitorGroupId IS NULL THEN 'Per-Customer Credentials'
        ELSE 'LM Disabled'
    END AS LMConfigStatus
FROM Customers
WHERE IsActive = 1
ORDER BY Name;
GO

PRINT 'Per-customer LogicMonitor configuration migration complete!';
GO
