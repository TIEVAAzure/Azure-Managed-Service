-- =====================================================
-- Performance Graphs & SKU-Based Recommendations
-- Created: 2026-01-19
-- =====================================================

-- =====================================================
-- 1. DEVICE METRIC HISTORY - Daily aggregates for 90 days
-- =====================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'LMDeviceMetricHistory')
BEGIN
    CREATE TABLE LMDeviceMetricHistory (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        CustomerId UNIQUEIDENTIFIER NOT NULL,
        DeviceId INT NOT NULL,
        MetricName NVARCHAR(50) NOT NULL,  -- CPU, Memory, Disk, etc.
        MetricDate DATE NOT NULL,
        AvgValue DECIMAL(10,2),
        MaxValue DECIMAL(10,2),
        MinValue DECIMAL(10,2),
        P95Value DECIMAL(10,2),           -- 95th percentile
        SampleCount INT DEFAULT 0,
        CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
        
        -- Indexes for efficient querying
        INDEX IX_MetricHistory_Customer_Device (CustomerId, DeviceId, MetricDate DESC),
        INDEX IX_MetricHistory_Lookup (CustomerId, DeviceId, MetricName, MetricDate DESC),
        
        -- Unique constraint to prevent duplicates
        CONSTRAINT UQ_DeviceMetricHistory UNIQUE (CustomerId, DeviceId, MetricName, MetricDate),
        
        -- Foreign key to Customers
        CONSTRAINT FK_MetricHistory_Customer FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
    );
    PRINT 'Created LMDeviceMetricHistory table';
END
ELSE
    PRINT 'LMDeviceMetricHistory table already exists';
GO

-- =====================================================
-- 2. AZURE SKU FAMILIES - Size ordering for right-sizing
-- =====================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AzureSkuFamilies')
BEGIN
    CREATE TABLE AzureSkuFamilies (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ResourceType NVARCHAR(50) NOT NULL,     -- VirtualMachine, ManagedDisk, AppServicePlan
        SkuFamily NVARCHAR(50) NOT NULL,        -- Dsv5, Esv5, Premium_LRS, P1v3
        SkuName NVARCHAR(100) NOT NULL,         -- Standard_D2s_v5, Standard_D4s_v5
        DisplayName NVARCHAR(100),              -- Friendly name
        SizeOrder INT NOT NULL,                 -- 1=smallest, 2, 3, 4... within family
        
        -- Capacity specs
        vCPUs INT,
        MemoryGB DECIMAL(10,2),
        MaxDataDisks INT,
        MaxIOPS INT,
        MaxThroughputMBps INT,
        TempStorageGB INT,
        
        -- Cost estimation (UK South, pay-as-you-go)
        HourlyCostEstimate DECIMAL(10,4),
        MonthlyCostEstimate DECIMAL(10,2),
        
        -- Metadata
        Notes NVARCHAR(500),
        IsActive BIT DEFAULT 1,
        CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 DEFAULT GETUTCDATE(),
        
        -- Indexes
        INDEX IX_SkuFamilies_Type_Family (ResourceType, SkuFamily, SizeOrder),
        INDEX IX_SkuFamilies_SkuName (SkuName),
        
        -- Unique SKU name per resource type
        CONSTRAINT UQ_SkuName UNIQUE (ResourceType, SkuName)
    );
    PRINT 'Created AzureSkuFamilies table';
END
ELSE
    PRINT 'AzureSkuFamilies table already exists';
GO

-- =====================================================
-- 3. ADD COLUMNS TO LMDeviceMetricsV2
-- =====================================================

-- Add CurrentSku column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMDeviceMetricsV2') AND name = 'CurrentSku')
BEGIN
    ALTER TABLE LMDeviceMetricsV2 ADD CurrentSku NVARCHAR(100);
    PRINT 'Added CurrentSku column to LMDeviceMetricsV2';
END

-- Add RecommendedSku column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMDeviceMetricsV2') AND name = 'RecommendedSku')
BEGIN
    ALTER TABLE LMDeviceMetricsV2 ADD RecommendedSku NVARCHAR(100);
    PRINT 'Added RecommendedSku column to LMDeviceMetricsV2';
END

-- Add SkuRecommendationReason column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMDeviceMetricsV2') AND name = 'SkuRecommendationReason')
BEGIN
    ALTER TABLE LMDeviceMetricsV2 ADD SkuRecommendationReason NVARCHAR(500);
    PRINT 'Added SkuRecommendationReason column to LMDeviceMetricsV2';
END

-- Add SkuFamily column (to match against AzureSkuFamilies)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMDeviceMetricsV2') AND name = 'SkuFamily')
BEGIN
    ALTER TABLE LMDeviceMetricsV2 ADD SkuFamily NVARCHAR(50);
    PRINT 'Added SkuFamily column to LMDeviceMetricsV2';
END

-- Add PotentialMonthlySavings column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMDeviceMetricsV2') AND name = 'PotentialMonthlySavings')
BEGIN
    ALTER TABLE LMDeviceMetricsV2 ADD PotentialMonthlySavings DECIMAL(10,2);
    PRINT 'Added PotentialMonthlySavings column to LMDeviceMetricsV2';
END

-- Add Metrics90DayJson for 90-day aggregates (used for recommendations)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMDeviceMetricsV2') AND name = 'Metrics90DayJson')
BEGIN
    ALTER TABLE LMDeviceMetricsV2 ADD Metrics90DayJson NVARCHAR(MAX);
    PRINT 'Added Metrics90DayJson column to LMDeviceMetricsV2';
END
GO

-- =====================================================
-- 3b. ADD COLUMNS TO LMSyncStatuses for History Sync
-- =====================================================

-- Add HistorySyncProgress column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMSyncStatuses') AND name = 'HistorySyncProgress')
BEGIN
    ALTER TABLE LMSyncStatuses ADD HistorySyncProgress INT;
    PRINT 'Added HistorySyncProgress column to LMSyncStatuses';
END

-- Add HistorySyncTotal column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMSyncStatuses') AND name = 'HistorySyncTotal')
BEGIN
    ALTER TABLE LMSyncStatuses ADD HistorySyncTotal INT;
    PRINT 'Added HistorySyncTotal column to LMSyncStatuses';
END

-- Add HistorySyncWithData column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMSyncStatuses') AND name = 'HistorySyncWithData')
BEGIN
    ALTER TABLE LMSyncStatuses ADD HistorySyncWithData INT;
    PRINT 'Added HistorySyncWithData column to LMSyncStatuses';
END

-- Add HistorySyncStarted column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMSyncStatuses') AND name = 'HistorySyncStarted')
BEGIN
    ALTER TABLE LMSyncStatuses ADD HistorySyncStarted DATETIME2;
    PRINT 'Added HistorySyncStarted column to LMSyncStatuses';
END

-- Add HistorySyncCompleted column
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('LMSyncStatuses') AND name = 'HistorySyncCompleted')
BEGIN
    ALTER TABLE LMSyncStatuses ADD HistorySyncCompleted DATETIME2;
    PRINT 'Added HistorySyncCompleted column to LMSyncStatuses';
END
GO

-- =====================================================
-- 4. SEED DATA - Common Azure VM SKU Families
-- =====================================================

-- D-series v5 (General Purpose)
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'Dsv5')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, vCPUs, MemoryGB, MaxDataDisks, MaxIOPS, MaxThroughputMBps, MonthlyCostEstimate, Notes)
    VALUES 
    ('VirtualMachine', 'Dsv5', 'Standard_D2s_v5', 'D2s v5 (2 vCPU, 8GB)', 1, 2, 8, 4, 3750, 85, 70, 'General purpose, balanced compute'),
    ('VirtualMachine', 'Dsv5', 'Standard_D4s_v5', 'D4s v5 (4 vCPU, 16GB)', 2, 4, 16, 8, 6400, 145, 140, 'General purpose, balanced compute'),
    ('VirtualMachine', 'Dsv5', 'Standard_D8s_v5', 'D8s v5 (8 vCPU, 32GB)', 3, 8, 32, 16, 12800, 290, 280, 'General purpose, balanced compute'),
    ('VirtualMachine', 'Dsv5', 'Standard_D16s_v5', 'D16s v5 (16 vCPU, 64GB)', 4, 16, 64, 32, 25600, 580, 560, 'General purpose, balanced compute'),
    ('VirtualMachine', 'Dsv5', 'Standard_D32s_v5', 'D32s v5 (32 vCPU, 128GB)', 5, 32, 128, 32, 51200, 865, 1120, 'General purpose, balanced compute'),
    ('VirtualMachine', 'Dsv5', 'Standard_D48s_v5', 'D48s v5 (48 vCPU, 192GB)', 6, 48, 192, 32, 76800, 1315, 1680, 'General purpose, balanced compute'),
    ('VirtualMachine', 'Dsv5', 'Standard_D64s_v5', 'D64s v5 (64 vCPU, 256GB)', 7, 64, 256, 32, 80000, 1735, 2240, 'General purpose, balanced compute');
    PRINT 'Seeded Dsv5 SKU family';
END

-- E-series v5 (Memory Optimized)
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'Esv5')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, vCPUs, MemoryGB, MaxDataDisks, MaxIOPS, MaxThroughputMBps, MonthlyCostEstimate, Notes)
    VALUES 
    ('VirtualMachine', 'Esv5', 'Standard_E2s_v5', 'E2s v5 (2 vCPU, 16GB)', 1, 2, 16, 4, 3750, 85, 90, 'Memory optimized, high memory-to-core ratio'),
    ('VirtualMachine', 'Esv5', 'Standard_E4s_v5', 'E4s v5 (4 vCPU, 32GB)', 2, 4, 32, 8, 6400, 145, 180, 'Memory optimized, high memory-to-core ratio'),
    ('VirtualMachine', 'Esv5', 'Standard_E8s_v5', 'E8s v5 (8 vCPU, 64GB)', 3, 8, 64, 16, 12800, 290, 360, 'Memory optimized, high memory-to-core ratio'),
    ('VirtualMachine', 'Esv5', 'Standard_E16s_v5', 'E16s v5 (16 vCPU, 128GB)', 4, 16, 128, 32, 25600, 580, 720, 'Memory optimized, high memory-to-core ratio'),
    ('VirtualMachine', 'Esv5', 'Standard_E32s_v5', 'E32s v5 (32 vCPU, 256GB)', 5, 32, 256, 32, 51200, 865, 1440, 'Memory optimized, high memory-to-core ratio'),
    ('VirtualMachine', 'Esv5', 'Standard_E48s_v5', 'E48s v5 (48 vCPU, 384GB)', 6, 48, 384, 32, 76800, 1315, 2160, 'Memory optimized, high memory-to-core ratio'),
    ('VirtualMachine', 'Esv5', 'Standard_E64s_v5', 'E64s v5 (64 vCPU, 512GB)', 7, 64, 512, 32, 80000, 1735, 2880, 'Memory optimized, high memory-to-core ratio');
    PRINT 'Seeded Esv5 SKU family';
END

-- B-series (Burstable)
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'Bs')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, vCPUs, MemoryGB, MaxDataDisks, MaxIOPS, MaxThroughputMBps, MonthlyCostEstimate, Notes)
    VALUES 
    ('VirtualMachine', 'Bs', 'Standard_B1s', 'B1s (1 vCPU, 1GB)', 1, 1, 1, 2, 320, 10, 7, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B1ms', 'B1ms (1 vCPU, 2GB)', 2, 1, 2, 2, 640, 10, 14, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B2s', 'B2s (2 vCPU, 4GB)', 3, 2, 4, 4, 1280, 10, 28, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B2ms', 'B2ms (2 vCPU, 8GB)', 4, 2, 8, 4, 1920, 23, 56, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B4ms', 'B4ms (4 vCPU, 16GB)', 5, 4, 16, 8, 2880, 35, 112, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B8ms', 'B8ms (8 vCPU, 32GB)', 6, 8, 32, 16, 4320, 50, 224, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B12ms', 'B12ms (12 vCPU, 48GB)', 7, 12, 48, 16, 6480, 50, 336, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B16ms', 'B16ms (16 vCPU, 64GB)', 8, 16, 64, 32, 8640, 50, 448, 'Burstable, for light workloads'),
    ('VirtualMachine', 'Bs', 'Standard_B20ms', 'B20ms (20 vCPU, 80GB)', 9, 20, 80, 32, 10800, 50, 560, 'Burstable, for light workloads');
    PRINT 'Seeded Bs SKU family';
END

-- F-series v2 (Compute Optimized)
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'Fsv2')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, vCPUs, MemoryGB, MaxDataDisks, MaxIOPS, MaxThroughputMBps, MonthlyCostEstimate, Notes)
    VALUES 
    ('VirtualMachine', 'Fsv2', 'Standard_F2s_v2', 'F2s v2 (2 vCPU, 4GB)', 1, 2, 4, 4, 3200, 47, 60, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F4s_v2', 'F4s v2 (4 vCPU, 8GB)', 2, 4, 8, 8, 6400, 95, 120, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F8s_v2', 'F8s v2 (8 vCPU, 16GB)', 3, 8, 16, 16, 12800, 190, 240, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F16s_v2', 'F16s v2 (16 vCPU, 32GB)', 4, 16, 32, 32, 25600, 380, 480, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F32s_v2', 'F32s v2 (32 vCPU, 64GB)', 5, 32, 64, 32, 51200, 750, 960, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F48s_v2', 'F48s v2 (48 vCPU, 96GB)', 6, 48, 96, 32, 76800, 1100, 1440, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F64s_v2', 'F64s v2 (64 vCPU, 128GB)', 7, 64, 128, 32, 80000, 1100, 1920, 'Compute optimized, high CPU-to-memory ratio'),
    ('VirtualMachine', 'Fsv2', 'Standard_F72s_v2', 'F72s v2 (72 vCPU, 144GB)', 8, 72, 144, 32, 80000, 1100, 2160, 'Compute optimized, high CPU-to-memory ratio');
    PRINT 'Seeded Fsv2 SKU family';
END

-- Premium SSD Managed Disks
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'Premium_LRS' AND ResourceType = 'ManagedDisk')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, MaxIOPS, MaxThroughputMBps, MonthlyCostEstimate, Notes)
    VALUES 
    ('ManagedDisk', 'Premium_LRS', 'P4', 'Premium SSD P4 (32 GB)', 1, 120, 25, 5, 'Premium SSD, 32 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P6', 'Premium SSD P6 (64 GB)', 2, 240, 50, 10, 'Premium SSD, 64 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P10', 'Premium SSD P10 (128 GB)', 3, 500, 100, 15, 'Premium SSD, 128 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P15', 'Premium SSD P15 (256 GB)', 4, 1100, 125, 25, 'Premium SSD, 256 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P20', 'Premium SSD P20 (512 GB)', 5, 2300, 150, 55, 'Premium SSD, 512 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P30', 'Premium SSD P30 (1 TB)', 6, 5000, 200, 100, 'Premium SSD, 1024 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P40', 'Premium SSD P40 (2 TB)', 7, 7500, 250, 180, 'Premium SSD, 2048 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P50', 'Premium SSD P50 (4 TB)', 8, 7500, 250, 350, 'Premium SSD, 4096 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P60', 'Premium SSD P60 (8 TB)', 9, 16000, 500, 650, 'Premium SSD, 8192 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P70', 'Premium SSD P70 (16 TB)', 10, 18000, 750, 1200, 'Premium SSD, 16384 GB'),
    ('ManagedDisk', 'Premium_LRS', 'P80', 'Premium SSD P80 (32 TB)', 11, 20000, 900, 2400, 'Premium SSD, 32767 GB');
    PRINT 'Seeded Premium SSD Managed Disk SKUs';
END

-- Standard SSD Managed Disks
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'StandardSSD_LRS' AND ResourceType = 'ManagedDisk')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, MaxIOPS, MaxThroughputMBps, MonthlyCostEstimate, Notes)
    VALUES 
    ('ManagedDisk', 'StandardSSD_LRS', 'E4', 'Standard SSD E4 (32 GB)', 1, 500, 60, 2, 'Standard SSD, 32 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E6', 'Standard SSD E6 (64 GB)', 2, 500, 60, 4, 'Standard SSD, 64 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E10', 'Standard SSD E10 (128 GB)', 3, 500, 60, 8, 'Standard SSD, 128 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E15', 'Standard SSD E15 (256 GB)', 4, 500, 60, 12, 'Standard SSD, 256 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E20', 'Standard SSD E20 (512 GB)', 5, 500, 60, 25, 'Standard SSD, 512 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E30', 'Standard SSD E30 (1 TB)', 6, 500, 60, 45, 'Standard SSD, 1024 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E40', 'Standard SSD E40 (2 TB)', 7, 500, 60, 85, 'Standard SSD, 2048 GB'),
    ('ManagedDisk', 'StandardSSD_LRS', 'E50', 'Standard SSD E50 (4 TB)', 8, 500, 60, 165, 'Standard SSD, 4096 GB');
    PRINT 'Seeded Standard SSD Managed Disk SKUs';
END

-- App Service Plan (Windows)
IF NOT EXISTS (SELECT 1 FROM AzureSkuFamilies WHERE SkuFamily = 'Pv3' AND ResourceType = 'AppServicePlan')
BEGIN
    INSERT INTO AzureSkuFamilies (ResourceType, SkuFamily, SkuName, DisplayName, SizeOrder, vCPUs, MemoryGB, MonthlyCostEstimate, Notes)
    VALUES 
    ('AppServicePlan', 'Pv3', 'P0v3', 'Premium P0v3 (1 vCPU, 4GB)', 1, 1, 4, 85, 'Premium v3 plan'),
    ('AppServicePlan', 'Pv3', 'P1v3', 'Premium P1v3 (2 vCPU, 8GB)', 2, 2, 8, 170, 'Premium v3 plan'),
    ('AppServicePlan', 'Pv3', 'P2v3', 'Premium P2v3 (4 vCPU, 16GB)', 3, 4, 16, 340, 'Premium v3 plan'),
    ('AppServicePlan', 'Pv3', 'P3v3', 'Premium P3v3 (8 vCPU, 32GB)', 4, 8, 32, 680, 'Premium v3 plan'),
    ('AppServicePlan', 'Sv1', 'S1', 'Standard S1 (1 vCPU, 1.75GB)', 1, 1, 1.75, 50, 'Standard plan'),
    ('AppServicePlan', 'Sv1', 'S2', 'Standard S2 (2 vCPU, 3.5GB)', 2, 2, 3.5, 100, 'Standard plan'),
    ('AppServicePlan', 'Sv1', 'S3', 'Standard S3 (4 vCPU, 7GB)', 3, 4, 7, 200, 'Standard plan'),
    ('AppServicePlan', 'Bv1', 'B1', 'Basic B1 (1 vCPU, 1.75GB)', 1, 1, 1.75, 38, 'Basic plan'),
    ('AppServicePlan', 'Bv1', 'B2', 'Basic B2 (2 vCPU, 3.5GB)', 2, 2, 3.5, 75, 'Basic plan'),
    ('AppServicePlan', 'Bv1', 'B3', 'Basic B3 (4 vCPU, 7GB)', 3, 4, 7, 150, 'Basic plan');
    PRINT 'Seeded App Service Plan SKUs';
END
GO

-- =====================================================
-- 5. RETENTION POLICY - Keep only 90 days of history
-- =====================================================
-- This can be run as a scheduled job daily

-- Create or replace the cleanup procedure
CREATE OR ALTER PROCEDURE CleanupMetricHistory
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATE = DATEADD(DAY, -90, GETUTCDATE());
    DECLARE @DeletedCount INT;
    
    -- Delete records older than 90 days
    DELETE FROM LMDeviceMetricHistory
    WHERE MetricDate < @CutoffDate;
    
    SET @DeletedCount = @@ROWCOUNT;
    
    PRINT 'Deleted ' + CAST(@DeletedCount AS VARCHAR) + ' metric history records older than ' + CAST(@CutoffDate AS VARCHAR);
END
GO

-- =====================================================
-- 6. VERIFY SETUP
-- =====================================================
SELECT 'Tables' as [Section], name as [Name], create_date 
FROM sys.tables 
WHERE name IN ('LMDeviceMetricHistory', 'AzureSkuFamilies', 'LMDeviceMetricsV2');

SELECT 'SKU Families' as [Section], ResourceType, SkuFamily, COUNT(*) as [SKU Count]
FROM AzureSkuFamilies
GROUP BY ResourceType, SkuFamily
ORDER BY ResourceType, SkuFamily;

SELECT 'LMDeviceMetricsV2 Columns' as [Section], name as [Column]
FROM sys.columns 
WHERE object_id = OBJECT_ID('LMDeviceMetricsV2')
AND name IN ('CurrentSku', 'RecommendedSku', 'SkuRecommendationReason', 'SkuFamily', 'PotentialMonthlySavings', 'Metrics90DayJson');
GO
