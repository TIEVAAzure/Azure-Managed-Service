-- Create CustomerReservationCache table for async reservation data processing
-- Run this in Azure SQL Database: TievaPortal

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CustomerReservationCache')
BEGIN
    CREATE TABLE CustomerReservationCache (
        Id uniqueidentifier NOT NULL PRIMARY KEY DEFAULT NEWID(),
        CustomerId uniqueidentifier NOT NULL,
        Status nvarchar(20) NOT NULL DEFAULT 'Pending',  -- Pending, Running, Completed, Failed
        LastRefreshed datetime2 NULL,
        ReservationsJson nvarchar(max) NULL,
        InsightsJson nvarchar(max) NULL,
        SummaryJson nvarchar(max) NULL,
        PurchaseRecommendationsJson nvarchar(max) NULL,
        ErrorsJson nvarchar(max) NULL,
        ErrorMessage nvarchar(max) NULL,
        CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
        
        CONSTRAINT FK_CustomerReservationCache_Customer 
            FOREIGN KEY (CustomerId) REFERENCES Customers(Id) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX IX_CustomerReservationCache_CustomerId 
        ON CustomerReservationCache(CustomerId);
    
    PRINT 'CustomerReservationCache table created successfully';
END
ELSE
BEGIN
    PRINT 'CustomerReservationCache table already exists';
END
