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
    
    // FinOps Configuration
    public string? FinOpsStorageAccount { get; set; }
    public string? FinOpsContainer { get; set; }
    public string? FinOpsPowerBIUrl { get; set; }
    public string? FinOpsSasKeyVaultRef { get; set; }
    public DateTime? FinOpsSasExpiry { get; set; }
    
    // LogicMonitor Integration
    public int? LogicMonitorGroupId { get; set; }
    
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