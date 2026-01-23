using System;
using System.Collections.Generic;

namespace TIEVA.Functions.Models;

// Service Tiers
public class ServiceTier
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public List<TierModule> TierModules { get; set; } = new();
}

// Assessment Modules
public class AssessmentModule
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public int EstimatedMinutes { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<TierModule> TierModules { get; set; } = new();
}

// Tier Module Mapping
public class TierModule
{
    public Guid Id { get; set; }
    public Guid TierId { get; set; }
    public Guid ModuleId { get; set; }
    public bool IsIncluded { get; set; } = true;
    public string Frequency { get; set; } = "Monthly";
    public ServiceTier? Tier { get; set; }
    public AssessmentModule? Module { get; set; }
}

// Team Members
public class TeamMember
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? AzureAdObjectId { get; set; }  // For future Azure AD integration
    public string? Role { get; set; }  // e.g., "Account Manager", "Technical Lead"
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Customers
public class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Industry { get; set; }
    public string? PrimaryContact { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? NextMeetingDate { get; set; }
    public bool SchedulingEnabled { get; set; } = true;

    // Team Lead Assignment
    public Guid? TeamLeadId { get; set; }
    public TeamMember? TeamLead { get; set; }
    
    // FinOps Configuration
    public string? FinOpsStorageAccount { get; set; }
    public string? FinOpsContainer { get; set; }
    public string? FinOpsPowerBIUrl { get; set; }
    public string? FinOpsSasKeyVaultRef { get; set; }
    public DateTime? FinOpsSasExpiry { get; set; }
    
    // LogicMonitor Integration
    public int? LogicMonitorGroupId { get; set; }  // For filtering within TIEVA's global LM portal
    public bool LMEnabled { get; set; } = false;   // Enable LM integration for this customer
    public bool LMHasCustomCredentials { get; set; } = false;  // True if customer has their own LM portal credentials
    // Note: Per-customer credentials stored in Key Vault as LM-{CustomerId}-Company, LM-{CustomerId}-AccessId, LM-{CustomerId}-AccessKey
    
    public List<AzureConnection> Connections { get; set; } = new();
    public List<Assessment> Assessments { get; set; } = new();
}

// Azure Connections
public class AzureConnection
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string SecretKeyVaultRef { get; set; } = string.Empty;
    public DateTime? SecretExpiry { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastValidated { get; set; }
    public string? LastValidationStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public Customer? Customer { get; set; }
    public List<CustomerSubscription> Subscriptions { get; set; } = new();
}

// Customer Subscriptions
public class CustomerSubscription
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public string SubscriptionId { get; set; } = string.Empty;
    public string? SubscriptionName { get; set; }
    public Guid? TierId { get; set; }
    public string Environment { get; set; } = "Production";
    public bool IsInScope { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public AzureConnection? Connection { get; set; }
    public ServiceTier? Tier { get; set; }
}

// Assessments
public class Assessment
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? ConnectionId { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? StartedBy { get; set; }
    public string TriggerType { get; set; } = "Manual";  // Manual, Scheduled, PreMeeting
    public decimal? ScoreOverall { get; set; }
    public int FindingsTotal { get; set; }
    public int FindingsHigh { get; set; }
    public int FindingsMedium { get; set; }
    public int FindingsLow { get; set; }
    public DateTime CreatedAt { get; set; }
    public Customer? Customer { get; set; }
    public AzureConnection? Connection { get; set; }
    public List<AssessmentModuleResult> ModuleResults { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
}

// Assessment Module Results
public class AssessmentModuleResult
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string? SubscriptionId { get; set; }
    public string Status { get; set; } = "Pending";
    public decimal? Score { get; set; }
    public int FindingsCount { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BlobPath { get; set; }
    public Assessment? Assessment { get; set; }
}

// Findings
public class Finding
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string? SubscriptionId { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceName { get; set; }
    public string? ResourceId { get; set; }
    public string FindingText { get; set; } = string.Empty;
    public string? Recommendation { get; set; }
    public decimal? EffortHours { get; set; }
    public string? Owner { get; set; }
    public string Status { get; set; } = "Open";  // Open, Resolved
    public string ChangeStatus { get; set; } = "New";  // New, Recurring, Resolved
    public string? Hash { get; set; }  // For matching findings across assessments
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int OccurrenceCount { get; set; } = 1;  // How many assessments this appeared in
    public Guid? PreviousFindingId { get; set; }  // Link to the same finding in previous assessment
    public Assessment? Assessment { get; set; }
}

// Customer Finding Summary - persistent record per unique finding per customer
public class CustomerFinding
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;  // Unique identifier for this finding type
    public string Severity { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string FindingText { get; set; } = string.Empty;
    public string? Recommendation { get; set; }
    public string Status { get; set; } = "Open";  // Open, Resolved
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public Guid? LastAssessmentId { get; set; }
    public Customer? Customer { get; set; }
}

// Effort Settings - Configurable effort estimation
public class EffortSetting
{
    public int Id { get; set; }
    public string? Category { get; set; }              // e.g., 'Orphaned Resource', 'Security'
    public string? Severity { get; set; }              // 'High', 'Medium', 'Low' (fallback)
    public string? RecommendationPattern { get; set; } // Optional: specific recommendation match
    public decimal BaseHours { get; set; } = 1;        // Fixed hours for this type
    public decimal PerResourceHours { get; set; } = 0; // Additional hours per resource
    public string? ImpactOverride { get; set; }        // Override impact: High, Medium, Low (null = use severity)
    public string? Description { get; set; }           // Explain what this covers
    public string? Owner { get; set; }                 // Default owner
    public bool IsActive { get; set; } = true;
    public int MatchPriority { get; set; } = 0;        // Higher = checked first
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

// Finding Metadata - Unified configuration for findings (effort, impact, operational metadata)
public class FindingMetadata
{
    public int Id { get; set; }
    
    // Pattern Matching
    public string? ModuleCode { get; set; }              // e.g., 'NETWORK', 'IDENTITY'
    public string? Category { get; set; }                // e.g., 'NSG Configuration'
    public string? FindingPattern { get; set; }          // Pattern match on finding text
    public string? RecommendationPattern { get; set; }   // Pattern match on recommendation
    
    // Effort & Impact (for Priority Matrix)
    public decimal BaseHours { get; set; } = 1;          // Fixed hours for this finding type
    public decimal PerResourceHours { get; set; } = 0;   // Additional hours per resource
    public string? ImpactOverride { get; set; }          // Override impact: High, Medium, Low (null = use severity)
    public string? DefaultOwner { get; set; }            // e.g., 'Network Team'
    
    // Operational Metadata
    public string DowntimeRequired { get; set; } = "None";       // None, Partial, Full
    public int DowntimeMinutes { get; set; } = 0;
    public bool ChangeControlRequired { get; set; } = false;
    public bool MaintenanceWindowRequired { get; set; } = false;
    public bool AffectsProduction { get; set; } = true;
    public string CostImplication { get; set; } = "None";        // None, Low, Medium, High
    public string Complexity { get; set; } = "Medium";           // Low, Medium, High
    public string RiskLevel { get; set; } = "Medium";            // Low, Medium, High
    public string? Notes { get; set; }
    
    // Matching Control
    public int MatchPriority { get; set; } = 0;          // Higher = matched first
    public bool IsActive { get; set; } = true;
    
    // Audit
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

// Customer Roadmap Plan - Saved wave assignments for remediation planning
public class CustomerRoadmapPlan
{
    public int Id { get; set; }
    public Guid CustomerId { get; set; }
    public string? Wave1Findings { get; set; }      // JSON array of finding IDs
    public string? Wave2Findings { get; set; }
    public string? Wave3Findings { get; set; }
    public string? SkippedFindings { get; set; }    // Explicitly skipped findings
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
}

// Customer Reservation Cache - Cached reservation data for async processing
public class CustomerReservationCache
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Status { get; set; } = "Pending";  // Pending, Running, Completed, Failed
    public DateTime? LastRefreshed { get; set; }
    public string? ReservationsJson { get; set; }    // Cached reservation data
    public string? InsightsJson { get; set; }        // Cached insights
    public string? SummaryJson { get; set; }         // Cached summary stats  
    public string? PurchaseRecommendationsJson { get; set; }  // Cached purchase recommendations
    public string? ErrorsJson { get; set; }          // Any errors during fetch
    public string? ErrorMessage { get; set; }        // Fatal error message
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
}

// LogicMonitor Device Cache - Cached device data synced from LogicMonitor API
public class LMDeviceCache
{
    public int Id { get; set; }                      // LogicMonitor device ID
    public Guid CustomerId { get; set; }             // TIEVA customer mapping
    public int LMGroupId { get; set; }               // LogicMonitor group ID
    public string DisplayName { get; set; } = string.Empty;
    public string? HostStatus { get; set; }          // normal, dead, dead-collector, unconfirmed
    public string? AlertStatus { get; set; }         // none, warning, error, critical
    public string? SystemProperties { get; set; }    // JSON - optional additional properties
    public DateTime LastSyncedAt { get; set; }       // When this record was last updated from LM
    public DateTime CreatedAt { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
}

// LogicMonitor Sync Status - Track sync operations per customer
public class LMSyncStatus
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime? LastSyncStarted { get; set; }
    public DateTime? LastSyncCompleted { get; set; }
    public string Status { get; set; } = "Never";   // Never, Running, Completed, Failed, SyncingPerformance
    public int? DeviceCount { get; set; }
    public int? AlertCount { get; set; }
    public string? ErrorMessage { get; set; }
    
    // Performance sync tracking
    public int? PerformanceSyncProgress { get; set; }      // Devices processed so far
    public int? PerformanceDevicesWithData { get; set; }   // Devices with actual metric data
    
    // History sync tracking (90-day data)
    public int? HistorySyncProgress { get; set; }          // Devices processed so far
    public int? HistorySyncTotal { get; set; }             // Total devices to process
    public int? HistorySyncWithData { get; set; }          // Devices with history data
    public DateTime? HistorySyncStarted { get; set; }
    public DateTime? HistorySyncCompleted { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
}

// LogicMonitor Alert Cache - Cached alert data synced from LogicMonitor API
public class LMAlertCache
{
    public string Id { get; set; } = string.Empty;   // LogicMonitor alert ID (string)
    public Guid CustomerId { get; set; }             // TIEVA customer mapping
    public int? DeviceId { get; set; }               // LogicMonitor device ID
    public string? DeviceDisplayName { get; set; }
    public string? MonitorObjectName { get; set; }
    public string? AlertValue { get; set; }
    public int Severity { get; set; }                // 4=Critical, 3=Error, 2=Warning, 1=Info
    public string? SeverityText { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool Cleared { get; set; }
    public bool Acked { get; set; }
    public bool InSDT { get; set; }
    public string? ResourceTemplateName { get; set; }
    public DateTime LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
}

// LogicMonitor Device Metrics - Cached performance metrics for right-sizing analysis
public class LMDeviceMetrics
{
    public int Id { get; set; }                      // Auto-generated
    public Guid CustomerId { get; set; }
    public int DeviceId { get; set; }                // LogicMonitor device ID
    public string DeviceName { get; set; } = string.Empty;
    
    // CPU Metrics (percentages)
    public decimal? CpuAvg1Hr { get; set; }
    public decimal? CpuMax1Hr { get; set; }
    public decimal? CpuAvg24Hr { get; set; }
    public decimal? CpuMax24Hr { get; set; }
    public decimal? CpuAvg7Day { get; set; }
    public decimal? CpuMax7Day { get; set; }
    public decimal? CpuAvg30Day { get; set; }
    public decimal? CpuMax30Day { get; set; }
    
    // Memory Metrics (percentages)
    public decimal? MemAvg1Hr { get; set; }
    public decimal? MemMax1Hr { get; set; }
    public decimal? MemAvg24Hr { get; set; }
    public decimal? MemMax24Hr { get; set; }
    public decimal? MemAvg7Day { get; set; }
    public decimal? MemMax7Day { get; set; }
    public decimal? MemAvg30Day { get; set; }
    public decimal? MemMax30Day { get; set; }
    public decimal? MemTotalGB { get; set; }
    
    // Disk Metrics (percentages)
    public decimal? DiskAvg1Hr { get; set; }
    public decimal? DiskMax1Hr { get; set; }
    public decimal? DiskAvg24Hr { get; set; }
    public decimal? DiskMax24Hr { get; set; }
    public decimal? DiskAvg7Day { get; set; }
    public decimal? DiskMax7Day { get; set; }
    public decimal? DiskAvg30Day { get; set; }
    public decimal? DiskMax30Day { get; set; }
    public decimal? DiskTotalGB { get; set; }
    public decimal? DiskUsedGB { get; set; }
    
    // Network Metrics (Mbps)
    public decimal? NetInAvg1Hr { get; set; }
    public decimal? NetInMax1Hr { get; set; }
    public decimal? NetOutAvg1Hr { get; set; }
    public decimal? NetOutMax1Hr { get; set; }
    public decimal? NetInAvg24Hr { get; set; }
    public decimal? NetOutAvg24Hr { get; set; }
    
    // Right-Sizing Recommendations
    public string? CpuRecommendation { get; set; }      // Oversized, Right-sized, Undersized, Unknown
    public string? MemRecommendation { get; set; }
    public string? DiskRecommendation { get; set; }
    public string? OverallRecommendation { get; set; }
    public string? PotentialSavings { get; set; }
    
    // Datasource tracking (for debugging and pattern discovery)
    public string? ResourceType { get; set; }           // Server, AzureVM, AzureSQL, etc.
    public string? DetectedResourceType { get; set; }   // Auto-detected from datasources
    public string? MatchedDatasources { get; set; }     // Which datasources we matched (JSON)
    public string? UnmatchedDatasources { get; set; }   // Datasources we couldn't match (for adding new patterns)
    public string? AvailableDatasources { get; set; }   // ALL datasources on this device (JSON array)
    
    // Metadata
    public DateTime LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
    
    // Helper method to calculate recommendation
    public void CalculateRecommendations()
    {
        // CPU: <20% avg over 7 days = oversized, >80% = undersized
        CpuRecommendation = CpuAvg7Day switch
        {
            null => "Unknown",
            < 20 => "Oversized",
            > 80 => "Undersized",
            _ => "Right-sized"
        };
        
        // Memory: <30% avg = oversized, >85% = undersized
        MemRecommendation = MemAvg7Day switch
        {
            null => "Unknown",
            < 30 => "Oversized",
            > 85 => "Undersized",
            _ => "Right-sized"
        };
        
        // Disk: <30% = oversized, >85% = undersized
        DiskRecommendation = DiskAvg7Day switch
        {
            null => "Unknown",
            < 30 => "Oversized",
            > 85 => "Undersized",
            _ => "Right-sized"
        };
        
        // Overall: Oversized if ANY metric is oversized, Undersized if ANY is undersized
        var recs = new[] { CpuRecommendation, MemRecommendation, DiskRecommendation };
        if (recs.Any(r => r == "Undersized"))
            OverallRecommendation = "Undersized";
        else if (recs.Any(r => r == "Oversized"))
            OverallRecommendation = "Oversized";
        else if (recs.All(r => r == "Unknown"))
            OverallRecommendation = "Unknown";
        else
            OverallRecommendation = "Right-sized";
    }
}

// LogicMonitor Datasource Mappings - Data-driven pattern matching
public class LMDatasourceMapping
{
    public int Id { get; set; }
    
    // Classification
    public string ResourceType { get; set; } = "Server";     // Server, AzureVM, AzureSQL, AppService, etc.
    public string MetricCategory { get; set; } = "CPU";      // CPU, Memory, Disk
    
    // Pattern matching
    public string DatasourcePattern { get; set; } = string.Empty;  // Pattern to match datasource name
    public string? DatapointPatterns { get; set; }           // Comma-separated datapoint names
    public string? ExcludePattern { get; set; }              // Pattern to exclude
    
    // Metadata
    public string? Description { get; set; }
    public int Priority { get; set; } = 100;                 // Higher = matched first
    public bool IsActive { get; set; } = true;
    
    // Audit
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    
    // Helper to get datapoints as array
    public string[] GetDatapointArray() => 
        DatapointPatterns?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) 
        ?? Array.Empty<string>();
}

// =====================================================
// PERFORMANCE MONITORING V2 - Data-Driven System
// =====================================================

/// <summary>
/// Defines a resource type and how to detect it
/// </summary>
public class LMResourceType
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public string? Icon { get; set; }
    public string DetectionPatternsJson { get; set; } = "[]";
    public int SortOrder { get; set; } = 100;
    public bool ShowInDashboard { get; set; } = true;
    public bool HasPerformanceMetrics { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    
    // Navigation
    public List<LMMetricMapping> MetricMappings { get; set; } = new();
    
    // Helper
    public string[] GetDetectionPatterns()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(DetectionPatternsJson ?? "[]") ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>
/// Defines which metrics to extract for a resource type
/// </summary>
public class LMMetricMapping
{
    public int Id { get; set; }
    public int ResourceTypeId { get; set; }
    public string MetricName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Unit { get; set; } = "%";
    public string DatasourcePatternsJson { get; set; } = "[]";
    public string DatapointPatternsJson { get; set; } = "[]";
    public decimal? WarningThreshold { get; set; }
    public decimal? CriticalThreshold { get; set; }
    public bool InvertThreshold { get; set; }
    public decimal? OversizedBelow { get; set; }
    public decimal? UndersizedAbove { get; set; }
    public int SortOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation
    public LMResourceType? ResourceType { get; set; }
    
    // Helpers
    public string[] GetDatasourcePatterns()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(DatasourcePatternsJson ?? "[]") ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
    
    public string[] GetDatapointPatterns()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(DatapointPatternsJson ?? "[]") ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>
/// Device metrics storage with flexible JSON
/// </summary>
public class LMDeviceMetricsV2
{
    public int Id { get; set; }
    public Guid CustomerId { get; set; }
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public int? ResourceTypeId { get; set; }
    public string? ResourceTypeCode { get; set; }
    public string? DetectedTypeCode { get; set; }
    public string? MetricsJson { get; set; }
    public string? OverallStatus { get; set; }
    public string? Recommendation { get; set; }
    public string? StatusDetails { get; set; }
    public string? AvailableDatasourcesJson { get; set; }
    public string? RawDataJson { get; set; }
    public DateTime LastSyncedAt { get; set; }
    public DateTime? LastMetricDataAt { get; set; }
    public string? SyncErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // SKU-based recommendations (new)
    public string? CurrentSku { get; set; }             // Current Azure SKU (e.g., Standard_D4s_v5)
    public string? SkuFamily { get; set; }              // SKU family (e.g., Dsv5)
    public string? RecommendedSku { get; set; }         // Suggested SKU based on 90-day analysis
    public string? SkuRecommendationReason { get; set; }// Why this SKU is recommended
    public decimal? PotentialMonthlySavings { get; set; }// Estimated savings from right-sizing
    public string? Metrics90DayJson { get; set; }       // 90-day aggregate metrics for recommendations
    
    // Navigation
    public Customer? Customer { get; set; }
    public LMResourceType? ResourceType { get; set; }
    
    // Helpers
    public Dictionary<string, MetricValue> GetMetrics()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, MetricValue>>(MetricsJson ?? "{}") ?? new(); }
        catch { return new(); }
    }
    
    public void SetMetrics(Dictionary<string, MetricValue> metrics)
    {
        MetricsJson = System.Text.Json.JsonSerializer.Serialize(metrics);
    }
    
    public List<string> GetAvailableDatasources()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(AvailableDatasourcesJson ?? "[]") ?? new(); }
        catch { return new(); }
    }
    
    public Metrics90Day? GetMetrics90Day()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Metrics90Day>(Metrics90DayJson ?? "{}"); }
        catch { return null; }
    }
    
    public void SetMetrics90Day(Metrics90Day metrics)
    {
        Metrics90DayJson = System.Text.Json.JsonSerializer.Serialize(metrics);
    }
}

/// <summary>
/// Metric value (stored in JSON)
/// </summary>
public class MetricValue
{
    public decimal? Avg { get; set; }
    public decimal? Max { get; set; }
    public decimal? Min { get; set; }
    public string? Status { get; set; }
    public string? Recommendation { get; set; }
}

// =====================================================
// PERFORMANCE GRAPHS & SKU RECOMMENDATIONS
// =====================================================

/// <summary>
/// Daily metric history for performance graphs (90-day retention)
/// </summary>
public class LMDeviceMetricHistory
{
    public int Id { get; set; }
    public Guid CustomerId { get; set; }
    public int DeviceId { get; set; }
    public string MetricName { get; set; } = string.Empty;  // CPU, Memory, Disk
    public DateTime MetricDate { get; set; }
    public decimal? AvgValue { get; set; }
    public decimal? MaxValue { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? P95Value { get; set; }
    public int SampleCount { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // Navigation
    public Customer? Customer { get; set; }
}

/// <summary>
/// Azure SKU family definitions for right-sizing recommendations
/// </summary>
public class AzureSkuFamily
{
    public int Id { get; set; }
    public string ResourceType { get; set; } = string.Empty;  // VirtualMachine, ManagedDisk, AppServicePlan
    public string SkuFamily { get; set; } = string.Empty;     // Dsv5, Esv5, Premium_LRS
    public string SkuName { get; set; } = string.Empty;       // Standard_D2s_v5
    public string? DisplayName { get; set; }
    public int SizeOrder { get; set; }                        // 1=smallest within family
    
    // Capacity specs
    public int? vCPUs { get; set; }
    public decimal? MemoryGB { get; set; }
    public int? MaxDataDisks { get; set; }
    public int? MaxIOPS { get; set; }
    public int? MaxThroughputMBps { get; set; }
    public int? TempStorageGB { get; set; }
    
    // Cost
    public decimal? HourlyCostEstimate { get; set; }
    public decimal? MonthlyCostEstimate { get; set; }
    
    // Metadata
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 90-day metric aggregates (stored in JSON)
/// </summary>
public class Metrics90Day
{
    public decimal? CpuAvg { get; set; }
    public decimal? CpuMax { get; set; }
    public decimal? CpuP95 { get; set; }
    public decimal? MemAvg { get; set; }
    public decimal? MemMax { get; set; }
    public decimal? MemP95 { get; set; }
    public decimal? DiskAvg { get; set; }
    public decimal? DiskMax { get; set; }
    public decimal? DiskP95 { get; set; }
    public int DataPoints { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
}