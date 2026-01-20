-- Migration: Add AvailableDatasources column to LMDeviceMetrics
-- Purpose: Store all datasources found on each device for analysis/debugging
-- This helps identify what patterns need to be added for unmatched devices

-- Add column if it doesn't exist
IF NOT EXISTS (
    SELECT * FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME = 'LMDeviceMetrics' AND COLUMN_NAME = 'AvailableDatasources'
)
BEGIN
    ALTER TABLE LMDeviceMetrics ADD AvailableDatasources NVARCHAR(MAX) NULL;
    PRINT 'Added AvailableDatasources column to LMDeviceMetrics';
END
ELSE
BEGIN
    PRINT 'AvailableDatasources column already exists';
END

-- Add column for resource type detection
IF NOT EXISTS (
    SELECT * FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME = 'LMDeviceMetrics' AND COLUMN_NAME = 'DetectedResourceType'
)
BEGIN
    ALTER TABLE LMDeviceMetrics ADD DetectedResourceType NVARCHAR(100) NULL;
    PRINT 'Added DetectedResourceType column to LMDeviceMetrics';
END
ELSE
BEGIN
    PRINT 'DetectedResourceType column already exists';
END

-- Verify
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME = 'LMDeviceMetrics'
ORDER BY ORDINAL_POSITION;
