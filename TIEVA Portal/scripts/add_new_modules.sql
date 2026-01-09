-- =============================================
-- TIEVA Portal: Add New Modules
-- Run this script against TievaPortal database
-- =============================================

-- Check existing modules
SELECT * FROM AssessmentModules ORDER BY SortOrder, Code;

-- =============================================
-- ADD NEW MODULES
-- =============================================

-- Security Posture Module
INSERT INTO AssessmentModules (Id, Code, Name, Description, Icon, Category, EstimatedMinutes, SortOrder, IsActive)
SELECT NEWID(), 'SECURITY', 'Security Posture', 'Microsoft Defender for Cloud alerts, Secure Score tracking, security recommendations', N'🔒', 'Security', 15, 60, 1
WHERE NOT EXISTS (SELECT 1 FROM AssessmentModules WHERE Code = 'SECURITY');

-- Patch Management Module
INSERT INTO AssessmentModules (Id, Code, Name, Description, Icon, Category, EstimatedMinutes, SortOrder, IsActive)
SELECT NEWID(), 'PATCH', 'Patch Management', 'VM patch compliance status, Azure Update Manager, update schedules', N'🩹', 'Operations', 10, 70, 1
WHERE NOT EXISTS (SELECT 1 FROM AssessmentModules WHERE Code = 'PATCH');

-- Performance Module
INSERT INTO AssessmentModules (Id, Code, Name, Description, Icon, Category, EstimatedMinutes, SortOrder, IsActive)
SELECT NEWID(), 'PERFORMANCE', 'Performance', 'VM right-sizing, storage performance, resource utilization, underutilized resources', N'⚡', 'Operations', 20, 80, 1
WHERE NOT EXISTS (SELECT 1 FROM AssessmentModules WHERE Code = 'PERFORMANCE');

-- Regulatory Compliance Module (separate from Azure Policy)
INSERT INTO AssessmentModules (Id, Code, Name, Description, Icon, Category, EstimatedMinutes, SortOrder, IsActive)
SELECT NEWID(), 'COMPLIANCE', 'Regulatory Compliance', 'Regulatory framework alignment (ISO 27001, SOC2, NIST, CIS benchmarks)', N'✅', 'Governance', 15, 55, 1
WHERE NOT EXISTS (SELECT 1 FROM AssessmentModules WHERE Code = 'COMPLIANCE');

-- =============================================
-- UPDATE EXISTING MODULES (rename for clarity)
-- =============================================

-- Rename Policy module to be clearer it's about Azure Policy
UPDATE AssessmentModules 
SET Name = 'Azure Policy', 
    Description = 'Azure Policy compliance, policy assignments, and remediation tasks' 
WHERE Code = 'POLICY';

-- Update Reservation module name
UPDATE AssessmentModules 
SET Name = 'Reservations & Savings'
WHERE Code = 'RESERVATION';

-- =============================================
-- VERIFY MODULES ADDED
-- =============================================
SELECT * FROM AssessmentModules ORDER BY SortOrder, Code;

-- =============================================
-- CONFIGURE TIER MODULE MAPPINGS
-- =============================================

-- Get module IDs for the new modules
DECLARE @SecurityModuleId UNIQUEIDENTIFIER = (SELECT Id FROM AssessmentModules WHERE Code = 'SECURITY');
DECLARE @PatchModuleId UNIQUEIDENTIFIER = (SELECT Id FROM AssessmentModules WHERE Code = 'PATCH');
DECLARE @PerformanceModuleId UNIQUEIDENTIFIER = (SELECT Id FROM AssessmentModules WHERE Code = 'PERFORMANCE');
DECLARE @ComplianceModuleId UNIQUEIDENTIFIER = (SELECT Id FROM AssessmentModules WHERE Code = 'COMPLIANCE');

-- Get tier IDs (using DisplayName for clarity)
DECLARE @StandardTierId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE DisplayName = 'Standard');
DECLARE @AdvancedTierId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE DisplayName = 'Advanced');
DECLARE @PremiumTierId UNIQUEIDENTIFIER = (SELECT Id FROM ServiceTiers WHERE DisplayName = 'Premium');

-- Debug: Show what we found
SELECT 'Module IDs' as Info, 
    @SecurityModuleId as Security, 
    @PatchModuleId as Patch, 
    @PerformanceModuleId as Performance, 
    @ComplianceModuleId as Compliance;

SELECT 'Tier IDs' as Info, 
    @StandardTierId as Standard, 
    @AdvancedTierId as Advanced, 
    @PremiumTierId as Premium;

-- =============================================
-- SECURITY MODULE - Advanced & Premium (Weekly)
-- =============================================
INSERT INTO TierModules (Id, TierId, ModuleId, IsIncluded, Frequency)
SELECT NEWID(), @AdvancedTierId, @SecurityModuleId, 1, 'Weekly'
WHERE @AdvancedTierId IS NOT NULL AND @SecurityModuleId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM TierModules WHERE TierId = @AdvancedTierId AND ModuleId = @SecurityModuleId);

INSERT INTO TierModules (Id, TierId, ModuleId, IsIncluded, Frequency)
SELECT NEWID(), @PremiumTierId, @SecurityModuleId, 1, 'Weekly'
WHERE @PremiumTierId IS NOT NULL AND @SecurityModuleId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM TierModules WHERE TierId = @PremiumTierId AND ModuleId = @SecurityModuleId);

-- =============================================
-- PATCH MODULE - Advanced & Premium (Weekly)
-- =============================================
INSERT INTO TierModules (Id, TierId, ModuleId, IsIncluded, Frequency)
SELECT NEWID(), @AdvancedTierId, @PatchModuleId, 1, 'Weekly'
WHERE @AdvancedTierId IS NOT NULL AND @PatchModuleId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM TierModules WHERE TierId = @AdvancedTierId AND ModuleId = @PatchModuleId);

INSERT INTO TierModules (Id, TierId, ModuleId, IsIncluded, Frequency)
SELECT NEWID(), @PremiumTierId, @PatchModuleId, 1, 'Weekly'
WHERE @PremiumTierId IS NOT NULL AND @PatchModuleId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM TierModules WHERE TierId = @PremiumTierId AND ModuleId = @PatchModuleId);

-- =============================================
-- PERFORMANCE MODULE - Premium Only (Monthly)
-- =============================================
INSERT INTO TierModules (Id, TierId, ModuleId, IsIncluded, Frequency)
SELECT NEWID(), @PremiumTierId, @PerformanceModuleId, 1, 'Monthly'
WHERE @PremiumTierId IS NOT NULL AND @PerformanceModuleId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM TierModules WHERE TierId = @PremiumTierId AND ModuleId = @PerformanceModuleId);

-- =============================================
-- COMPLIANCE MODULE - Premium Only (Monthly)
-- =============================================
INSERT INTO TierModules (Id, TierId, ModuleId, IsIncluded, Frequency)
SELECT NEWID(), @PremiumTierId, @ComplianceModuleId, 1, 'Monthly'
WHERE @PremiumTierId IS NOT NULL AND @ComplianceModuleId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM TierModules WHERE TierId = @PremiumTierId AND ModuleId = @ComplianceModuleId);

-- =============================================
-- VERIFY TIER-MODULE MAPPINGS
-- =============================================
SELECT 
    st.DisplayName as Tier,
    am.Code as ModuleCode,
    am.Name as ModuleName,
    am.Icon,
    tm.Frequency,
    tm.IsIncluded
FROM TierModules tm
JOIN ServiceTiers st ON tm.TierId = st.Id
JOIN AssessmentModules am ON tm.ModuleId = am.Id
ORDER BY 
    CASE st.DisplayName 
        WHEN 'Premium' THEN 1 
        WHEN 'Advanced' THEN 2 
        WHEN 'Standard' THEN 3 
        ELSE 4 
    END, 
    am.SortOrder,
    am.Code;
