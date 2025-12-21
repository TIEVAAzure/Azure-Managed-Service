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