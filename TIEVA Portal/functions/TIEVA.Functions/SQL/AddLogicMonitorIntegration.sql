-- ============================================================================
-- TIEVA Portal - LogicMonitor Integration Migration
-- Date: January 2025
-- Description: Adds LogicMonitor group mapping to Customers table
-- ============================================================================

-- ============================================================================
-- 1. Add LogicMonitorGroupId to Customers table
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
ELSE
BEGIN
    PRINT 'LogicMonitorGroupId column already exists';
END
GO

-- ============================================================================
-- 2. Create index for faster lookups
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes 
    WHERE object_id = OBJECT_ID('Customers') AND name = 'IX_Customers_LogicMonitorGroupId'
)
BEGIN
    CREATE INDEX IX_Customers_LogicMonitorGroupId 
    ON Customers(LogicMonitorGroupId) 
    WHERE LogicMonitorGroupId IS NOT NULL;
    
    PRINT 'Created index IX_Customers_LogicMonitorGroupId';
END
GO

-- ============================================================================
-- 3. Pre-populate known mappings based on customer name matching
-- (These are the mappings from LogicMonitor hierarchy)
-- ============================================================================

-- Meeron Limited → LM Group 147
UPDATE Customers 
SET LogicMonitorGroupId = 147 
WHERE Name = 'Meeron Limited' AND LogicMonitorGroupId IS NULL;

-- Moorhouse → LM Group 393
UPDATE Customers 
SET LogicMonitorGroupId = 393 
WHERE Name = 'Moorhouse' AND LogicMonitorGroupId IS NULL;

-- Ovarro → LM Group 408
UPDATE Customers 
SET LogicMonitorGroupId = 408 
WHERE Name = 'Ovarro' AND LogicMonitorGroupId IS NULL;

-- Protein Works → LM Group 37
UPDATE Customers 
SET LogicMonitorGroupId = 37 
WHERE Name = 'Protein Works' AND LogicMonitorGroupId IS NULL;

-- Topps Tiles → LM Group 38
UPDATE Customers 
SET LogicMonitorGroupId = 38 
WHERE Name = 'Topps Tiles' AND LogicMonitorGroupId IS NULL;

PRINT 'Updated known customer mappings';
GO

-- ============================================================================
-- 4. Verify mappings
-- ============================================================================
SELECT 
    Id,
    Name,
    LogicMonitorGroupId,
    CASE 
        WHEN LogicMonitorGroupId IS NOT NULL THEN 'Mapped'
        ELSE 'Not Mapped'
    END AS LMStatus
FROM Customers
WHERE IsActive = 1
ORDER BY Name;
GO

PRINT 'LogicMonitor integration migration complete!';
GO
