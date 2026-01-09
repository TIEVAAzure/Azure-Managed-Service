namespace TIEVA.Core.Models
{
    public class ServiceTier
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Color { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        
        public ICollection<TierModule> TierModules { get; set; } = new List<TierModule>();
        public ICollection<CustomerSubscription> Subscriptions { get; set; } = new List<CustomerSubscription>();
    }

    public class AssessmentModule
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Category { get; set; }
        public int EstimatedMinutes { get; set; } = 5;
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public ICollection<TierModule> TierModules { get; set; } = new List<TierModule>();
    }

    public class TierModule
    {
        public Guid Id { get; set; }
        public Guid TierId { get; set; }
        public Guid ModuleId { get; set; }
        public bool IsIncluded { get; set; } = true;
        public string? Frequency { get; set; }
        
        public ServiceTier Tier { get; set; } = null!;
        public AssessmentModule Module { get; set; } = null!;
    }

    public class Customer
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Code { get; set; }
        public string? Industry { get; set; }
        public string? PrimaryContact { get; set; }
        public string? Email { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        
        public ICollection<AzureConnection> Connections { get; set; } = new List<AzureConnection>();
        public ICollection<CustomerSubscription> Subscriptions { get; set; } = new List<CustomerSubscription>();
        public ICollection<Assessment> Assessments { get; set; } = new List<Assessment>();
    }

    public class AzureConnection
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public string? DisplayName { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string? TenantName { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? SecretKeyVaultUri { get; set; }
        public DateTime? SecretExpiryDate { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? LastValidated { get; set; }
        public string? LastValidationError { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public string? Notes { get; set; }
        
        public Customer Customer { get; set; } = null!;
        public ICollection<CustomerSubscription> Subscriptions { get; set; } = new List<CustomerSubscription>();
    }

    public class CustomerSubscription
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public Guid ConnectionId { get; set; }
        public string SubscriptionId { get; set; } = string.Empty;
        public string? SubscriptionName { get; set; }
        public Guid? TierId { get; set; }
        public string? Environment { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsInScope { get; set; } = true;
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        
        public Customer Customer { get; set; } = null!;
        public AzureConnection Connection { get; set; } = null!;
        public ServiceTier? Tier { get; set; }
    }

    public class Assessment
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public Guid? ConnectionId { get; set; }
        public string Status { get; set; } = "Pending";
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? StartedBy { get; set; }
        public int? SubscriptionCount { get; set; }
        public int? ResourceCount { get; set; }
        public int? FindingCount { get; set; }
        public int HighFindings { get; set; }
        public int MediumFindings { get; set; }
        public int LowFindings { get; set; }
        public decimal? ScoreOverall { get; set; }
        public string? ResultsBlobUrl { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public Customer Customer { get; set; } = null!;
        public AzureConnection? Connection { get; set; }
        public ICollection<AssessmentModuleResult> ModuleResults { get; set; } = new List<AssessmentModuleResult>();
        public ICollection<Finding> Findings { get; set; } = new List<Finding>();
    }

    public class AssessmentModuleResult
    {
        public Guid Id { get; set; }
        public Guid AssessmentId { get; set; }
        public string ModuleCode { get; set; } = string.Empty;
        public string? SubscriptionId { get; set; }
        public string? Status { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? ItemCount { get; set; }
        public int? FindingCount { get; set; }
        public decimal? Score { get; set; }
        public string? ErrorMessage { get; set; }
        
        public Assessment Assessment { get; set; } = null!;
    }

    public class Finding
    {
        public Guid Id { get; set; }
        public Guid AssessmentId { get; set; }
        public string? ModuleCode { get; set; }
        public string? Severity { get; set; }
        public string? Category { get; set; }
        public string? SubscriptionId { get; set; }
        public string? SubscriptionName { get; set; }
        public string? ResourceGroup { get; set; }
        public string? ResourceType { get; set; }
        public string? ResourceName { get; set; }
        public string? FindingText { get; set; }
        public string? Recommendation { get; set; }
        public string? Pillar { get; set; }
        public decimal? EffortHours { get; set; }
        public string? Owner { get; set; }
        public string Status { get; set; } = "Open";
        public string? Hash { get; set; }
        public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedAt { get; set; }
        
        public Assessment Assessment { get; set; } = null!;
    }
}

namespace TIEVA.Core.DTOs
{
    public record CustomerDto(
        Guid Id,
        string Name,
        string? Code,
        string? Industry,
        string? PrimaryContact,
        string? Email,
        bool IsActive,
        int ConnectionCount,
        int SubscriptionCount,
        DateTime? LastAssessment,
        decimal? LastScore
    );

    public record CustomerCreateDto(
        string Name,
        string? Code,
        string? Industry,
        string? PrimaryContact,
        string? Email,
        string? Notes
    );

    public record ConnectionDto(
        Guid Id,
        Guid CustomerId,
        string? DisplayName,
        string TenantId,
        string? TenantName,
        string ClientId,
        DateTime? SecretExpiryDate,
        bool IsActive,
        DateTime? LastValidated,
        string? LastValidationError,
        int SubscriptionCount
    );

    public record ConnectionCreateDto(
        Guid CustomerId,
        string DisplayName,
        string TenantId,
        string? TenantName,
        string ClientId,
        string ClientSecret,
        DateTime? SecretExpiryDate
    );

    public record SubscriptionDto(
        Guid Id,
        string SubscriptionId,
        string? SubscriptionName,
        Guid? TierId,
        string? TierName,
        string? Environment,
        bool IsInScope,
        DateTime? LastAssessed
    );

    public record SubscriptionUpdateDto(
        Guid? TierId,
        string? Environment,
        bool IsInScope
    );

    public record ServiceTierDto(
        Guid Id,
        string Name,
        string? DisplayName,
        string? Description,
        string? Color,
        int ModuleCount,
        int SubscriptionCount,
        List<TierModuleDto> Modules
    );

    public record TierModuleDto(
        Guid ModuleId,
        string Code,
        string Name,
        string? Icon,
        bool IsIncluded,
        string? Frequency
    );

    public record AssessmentDto(
        Guid Id,
        Guid CustomerId,
        string CustomerName,
        string Status,
        DateTime? StartedAt,
        DateTime? CompletedAt,
        int? SubscriptionCount,
        int? FindingCount,
        int HighFindings,
        int MediumFindings,
        int LowFindings,
        decimal? ScoreOverall
    );

    public record AssessmentStartDto(
        Guid CustomerId,
        Guid ConnectionId,
        List<string>? SubscriptionIds,
        List<string>? ModuleCodes,
        bool OverrideTier = false
    );

    public record ValidationResult(
        bool IsValid,
        List<string> AccessibleSubscriptions,
        string? ErrorMessage
    );
}