using Microsoft.EntityFrameworkCore;
using TIEVA.Functions.Models;

namespace TIEVA.Functions.Services;

public class TievaDbContext : DbContext
{
    public TievaDbContext(DbContextOptions<TievaDbContext> options) : base(options) { }

    public DbSet<ServiceTier> ServiceTiers => Set<ServiceTier>();
    public DbSet<AssessmentModule> AssessmentModules => Set<AssessmentModule>();
    public DbSet<TierModule> TierModules => Set<TierModule>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AzureConnection> AzureConnections => Set<AzureConnection>();
    public DbSet<CustomerSubscription> CustomerSubscriptions => Set<CustomerSubscription>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AssessmentModuleResult> AssessmentModuleResults => Set<AssessmentModuleResult>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<CustomerFinding> CustomerFindings => Set<CustomerFinding>();
    public DbSet<EffortSetting> EffortSettings => Set<EffortSetting>();
    public DbSet<FindingMetadata> FindingMetadata => Set<FindingMetadata>();
    public DbSet<CustomerRoadmapPlan> CustomerRoadmapPlans => Set<CustomerRoadmapPlan>();
    public DbSet<CustomerReservationCache> CustomerReservationCache => Set<CustomerReservationCache>();
    public DbSet<LMDeviceCache> LMDevices => Set<LMDeviceCache>();
    public DbSet<LMAlertCache> LMAlerts => Set<LMAlertCache>();
    public DbSet<LMSyncStatus> LMSyncStatuses => Set<LMSyncStatus>();
    public DbSet<LMDeviceMetrics> LMDeviceMetrics => Set<LMDeviceMetrics>();
    public DbSet<LMDatasourceMapping> LMDatasourceMappings => Set<LMDatasourceMapping>();
    
    // Performance Monitoring V2
    public DbSet<LMResourceType> LMResourceTypes => Set<LMResourceType>();
    public DbSet<LMMetricMapping> LMMetricMappings => Set<LMMetricMapping>();
    public DbSet<LMDeviceMetricsV2> LMDeviceMetricsV2 => Set<LMDeviceMetricsV2>();
    
    // Performance Graphs & SKU Recommendations
    public DbSet<LMDeviceMetricHistory> LMDeviceMetricHistory => Set<LMDeviceMetricHistory>();
    public DbSet<AzureSkuFamily> AzureSkuFamilies => Set<AzureSkuFamily>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ServiceTier
        modelBuilder.Entity<ServiceTier>(e =>
        {
            e.ToTable("ServiceTiers");
            e.HasKey(x => x.Id);
        });

        // AssessmentModule
        modelBuilder.Entity<AssessmentModule>(e =>
        {
            e.ToTable("AssessmentModules");
            e.HasKey(x => x.Id);
        });

        // TierModule
        modelBuilder.Entity<TierModule>(e =>
        {
            e.ToTable("TierModules");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Tier).WithMany(x => x.TierModules).HasForeignKey(x => x.TierId);
            e.HasOne(x => x.Module).WithMany(x => x.TierModules).HasForeignKey(x => x.ModuleId);
        });

        // Customer
        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.HasKey(x => x.Id);
        });

        // AzureConnection
        modelBuilder.Entity<AzureConnection>(e =>
        {
            e.ToTable("AzureConnections");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany(x => x.Connections).HasForeignKey(x => x.CustomerId);
        });

        // CustomerSubscription
        modelBuilder.Entity<CustomerSubscription>(e =>
        {
            e.ToTable("CustomerSubscriptions");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Connection).WithMany(x => x.Subscriptions).HasForeignKey(x => x.ConnectionId);
            e.HasOne(x => x.Tier).WithMany().HasForeignKey(x => x.TierId);
        });

        // Assessment
        modelBuilder.Entity<Assessment>(e =>
        {
            e.ToTable("Assessments");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany(x => x.Assessments).HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.Connection).WithMany().HasForeignKey(x => x.ConnectionId);
        });

        // AssessmentModuleResult
        modelBuilder.Entity<AssessmentModuleResult>(e =>
        {
            e.ToTable("AssessmentModuleResults");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Assessment).WithMany(x => x.ModuleResults).HasForeignKey(x => x.AssessmentId);
        });

        // Finding
        modelBuilder.Entity<Finding>(e =>
        {
            e.ToTable("Findings");
            e.HasKey(x => x.Id);
            e.Property(x => x.FindingText).HasColumnName("Finding");
            e.HasOne(x => x.Assessment).WithMany(x => x.Findings).HasForeignKey(x => x.AssessmentId);
            e.HasIndex(x => x.Hash);  // Fast lookup for matching
        });

        // CustomerFinding - persistent findings per customer
        modelBuilder.Entity<CustomerFinding>(e =>
        {
            e.ToTable("CustomerFindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.FindingText).HasColumnName("Finding");
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => new { x.CustomerId, x.Hash }).IsUnique();  // One record per unique finding per customer
        });

        // EffortSettings - configurable effort estimation
        modelBuilder.Entity<EffortSetting>(e =>
        {
            e.ToTable("EffortSettings");
            e.HasKey(x => x.Id);
            e.Property(x => x.BaseHours).HasColumnType("decimal(6,2)");
            e.Property(x => x.PerResourceHours).HasColumnType("decimal(6,2)");
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.Severity);
            e.HasIndex(x => x.MatchPriority);
        });

        // FindingMetadata - unified finding configuration
        modelBuilder.Entity<FindingMetadata>(e =>
        {
            e.ToTable("FindingMetadata");
            e.HasKey(x => x.Id);
            e.Property(x => x.BaseHours).HasColumnType("decimal(6,2)");
            e.Property(x => x.PerResourceHours).HasColumnType("decimal(6,2)");
            e.HasIndex(x => x.ModuleCode);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.MatchPriority);
            e.HasIndex(x => x.IsActive);
        });

        // CustomerRoadmapPlan - saved wave assignments
        modelBuilder.Entity<CustomerRoadmapPlan>(e =>
        {
            e.ToTable("CustomerRoadmapPlans");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId).IsUnique();  // One plan per customer
        });

        // CustomerReservationCache - cached reservation data for async processing
        modelBuilder.Entity<CustomerReservationCache>(e =>
        {
            e.ToTable("CustomerReservationCache");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId).IsUnique();  // One cache per customer
        });

        // LMDeviceCache - cached LogicMonitor device data
        modelBuilder.Entity<LMDeviceCache>(e =>
        {
            e.ToTable("LMDevices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); // LM provides the ID
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId);  // Fast lookup by customer
            e.HasIndex(x => x.LMGroupId);   // Fast lookup by group
        });

        // LMAlertCache - cached LogicMonitor alert data
        modelBuilder.Entity<LMAlertCache>(e =>
        {
            e.ToTable("LMAlerts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); // LM provides the ID
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId);  // Fast lookup by customer
            e.HasIndex(x => x.Severity);    // Fast lookup by severity
        });

        // LMSyncStatus - track sync operations per customer
        modelBuilder.Entity<LMSyncStatus>(e =>
        {
            e.ToTable("LMSyncStatuses");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId).IsUnique();  // One status per customer
        });

        // LMDeviceMetrics - cached performance metrics for right-sizing
        modelBuilder.Entity<LMDeviceMetrics>(e =>
        {
            e.ToTable("LMDeviceMetrics");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => new { x.CustomerId, x.DeviceId }).IsUnique();
            e.HasIndex(x => x.OverallRecommendation);
        });

        // LMDatasourceMappings - data-driven metric patterns
        modelBuilder.Entity<LMDatasourceMapping>(e =>
        {
            e.ToTable("LMDatasourceMappings");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IsActive, x.MetricCategory, x.Priority });
        });

        // =====================================================
        // Performance Monitoring V2
        // =====================================================
        
        // LMResourceTypes - resource type definitions
        modelBuilder.Entity<LMResourceType>(e =>
        {
            e.ToTable("LMResourceTypes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.IsActive);
        });

        // LMMetricMappings - metric definitions per resource type
        modelBuilder.Entity<LMMetricMapping>(e =>
        {
            e.ToTable("LMMetricMappings");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.ResourceType).WithMany(x => x.MetricMappings).HasForeignKey(x => x.ResourceTypeId);
            e.HasIndex(x => x.ResourceTypeId);
        });

        // LMDeviceMetricsV2 - device metrics with flexible JSON storage
        modelBuilder.Entity<LMDeviceMetricsV2>(e =>
        {
            e.ToTable("LMDeviceMetricsV2");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.ResourceType).WithMany().HasForeignKey(x => x.ResourceTypeId);
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => new { x.CustomerId, x.DeviceId }).IsUnique();
            e.HasIndex(x => x.ResourceTypeCode);
            e.HasIndex(x => x.OverallStatus);
        });

        // =====================================================
        // Performance Graphs & SKU Recommendations
        // =====================================================
        
        // LMDeviceMetricHistory - daily aggregates for 90-day graphs
        modelBuilder.Entity<LMDeviceMetricHistory>(e =>
        {
            e.ToTable("LMDeviceMetricHistory");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => new { x.CustomerId, x.DeviceId, x.MetricDate });
            e.HasIndex(x => new { x.CustomerId, x.DeviceId, x.MetricName, x.MetricDate }).IsUnique();
        });

        // AzureSkuFamilies - SKU definitions for right-sizing
        modelBuilder.Entity<AzureSkuFamily>(e =>
        {
            e.ToTable("AzureSkuFamilies");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ResourceType, x.SkuFamily, x.SizeOrder });
            e.HasIndex(x => x.SkuName);
            e.HasIndex(x => new { x.ResourceType, x.SkuName }).IsUnique();
        });
    }
}