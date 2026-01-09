// =====================================================
// TIEVA.Infrastructure - Database Context
// File: Data/TievaDbContext.cs
// =====================================================

using Microsoft.EntityFrameworkCore;
using TIEVA.Core.Models;

namespace TIEVA.Infrastructure.Data;

public class TievaDbContext : DbContext
{
    public TievaDbContext(DbContextOptions<TievaDbContext> options) : base(options)
    {
    }
    
    public DbSet<ServiceTier> ServiceTiers => Set<ServiceTier>();
    public DbSet<AssessmentModule> AssessmentModules => Set<AssessmentModule>();
    public DbSet<TierModule> TierModules => Set<TierModule>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AzureConnection> AzureConnections => Set<AzureConnection>();
    public DbSet<CustomerSubscription> CustomerSubscriptions => Set<CustomerSubscription>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AssessmentModuleResult> AssessmentModuleResults => Set<AssessmentModuleResult>();
    public DbSet<Finding> Findings => Set<Finding>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // ServiceTier
        modelBuilder.Entity<ServiceTier>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.Color).HasMaxLength(20);
        });
        
        // AssessmentModule
        modelBuilder.Entity<AssessmentModule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => e.Code).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Icon).HasMaxLength(10);
            entity.Property(e => e.Category).HasMaxLength(100);
        });
        
        // TierModule
        modelBuilder.Entity<TierModule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TierId, e.ModuleId }).IsUnique();
            entity.Property(e => e.Frequency).HasMaxLength(50);
            
            entity.HasOne(e => e.Tier)
                .WithMany(t => t.TierModules)
                .HasForeignKey(e => e.TierId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.Module)
                .WithMany(m => m.TierModules)
                .HasForeignKey(e => e.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        
        // Customer
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(50);
            entity.HasIndex(e => e.Code).IsUnique().HasFilter("[Code] IS NOT NULL");
            entity.Property(e => e.Industry).HasMaxLength(100);
            entity.Property(e => e.PrimaryContact).HasMaxLength(200);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.CreatedBy).HasMaxLength(200);
        });
        
        // AzureConnection
        modelBuilder.Entity<AzureConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.TenantId).HasMaxLength(36).IsRequired();
            entity.Property(e => e.TenantName).HasMaxLength(200);
            entity.Property(e => e.ClientId).HasMaxLength(36).IsRequired();
            entity.Property(e => e.SecretKeyVaultUri).HasMaxLength(500);
            entity.Property(e => e.CreatedBy).HasMaxLength(200);
            
            entity.HasOne(e => e.Customer)
                .WithMany(c => c.Connections)
                .HasForeignKey(e => e.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        
        // CustomerSubscription
        modelBuilder.Entity<CustomerSubscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SubscriptionId).HasMaxLength(36).IsRequired();
            entity.Property(e => e.SubscriptionName).HasMaxLength(200);
            entity.Property(e => e.Environment).HasMaxLength(50);
            entity.HasIndex(e => new { e.CustomerId, e.SubscriptionId }).IsUnique();
            
            entity.HasOne(e => e.Customer)
                .WithMany(c => c.Subscriptions)
                .HasForeignKey(e => e.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.Connection)
                .WithMany(c => c.Subscriptions)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.NoAction);
                
            entity.HasOne(e => e.Tier)
                .WithMany(t => t.Subscriptions)
                .HasForeignKey(e => e.TierId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        
        // Assessment
        modelBuilder.Entity<Assessment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();
            entity.Property(e => e.StartedBy).HasMaxLength(200);
            entity.Property(e => e.ResultsBlobUrl).HasMaxLength(500);
            entity.Property(e => e.ScoreOverall).HasPrecision(5, 2);
            
            entity.HasOne(e => e.Customer)
                .WithMany(c => c.Assessments)
                .HasForeignKey(e => e.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.Connection)
                .WithMany()
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        
        // AssessmentModuleResult
        modelBuilder.Entity<AssessmentModuleResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ModuleCode).HasMaxLength(50).IsRequired();
            entity.Property(e => e.SubscriptionId).HasMaxLength(36);
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.Score).HasPrecision(5, 2);
            
            entity.HasOne(e => e.Assessment)
                .WithMany(a => a.ModuleResults)
                .HasForeignKey(e => e.AssessmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        
        // Finding
        modelBuilder.Entity<Finding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ModuleCode).HasMaxLength(50);
            entity.Property(e => e.Severity).HasMaxLength(20);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.SubscriptionId).HasMaxLength(36);
            entity.Property(e => e.SubscriptionName).HasMaxLength(200);
            entity.Property(e => e.ResourceGroup).HasMaxLength(200);
            entity.Property(e => e.ResourceType).HasMaxLength(200);
            entity.Property(e => e.ResourceName).HasMaxLength(500);
            entity.Property(e => e.Pillar).HasMaxLength(100);
            entity.Property(e => e.Owner).HasMaxLength(100);
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.Hash).HasMaxLength(64);
            entity.Property(e => e.EffortHours).HasPrecision(10, 2);
            
            // Map FindingText property to Finding column
            entity.Property(e => e.FindingText).HasColumnName("Finding");
            
            entity.HasOne(e => e.Assessment)
                .WithMany(a => a.Findings)
                .HasForeignKey(e => e.AssessmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
