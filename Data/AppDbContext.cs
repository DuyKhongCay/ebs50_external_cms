using ebs50_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Data;

/// <summary>
/// Entity Framework Core database context for the ESL CMS management system.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MachineState> MachineStates => Set<MachineState>();

    public DbSet<ModelItem> ModelItems => Set<ModelItem>();

    public DbSet<EslTag> EslTags => Set<EslTag>();

    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MachineState>(entity =>
        {
            entity.HasKey(e => e.StateCode);
            entity.Property(e => e.StateCode).ValueGeneratedNever();
            entity.Property(e => e.StateNameVi).IsRequired().HasMaxLength(100);
            entity.Property(e => e.StateNameKo).HasMaxLength(100);
            entity.Property(e => e.IconFileName).HasMaxLength(100);
            entity.Property(e => e.ThemeColor).HasMaxLength(20).HasDefaultValue("Black");
        });

        modelBuilder.Entity<ModelItem>(entity =>
        {
            entity.HasKey(e => e.ModelCode);
            entity.Property(e => e.ModelCode).HasMaxLength(50);
            entity.Property(e => e.Description).HasMaxLength(200);
        });

        modelBuilder.Entity<EslTag>(entity =>
        {
            entity.HasKey(e => e.MacAddress);
            entity.Property(e => e.MacAddress).HasMaxLength(16);
            entity.Property(e => e.MachineNo).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SyncStatus).HasMaxLength(20).HasDefaultValue("Pending");

            entity.HasOne(e => e.Model)
                .WithMany()
                .HasForeignKey(e => e.ModelCode)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.CurrentState)
                .WithMany()
                .HasForeignKey(e => e.CurrentStateCode)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.MachineNo);
            entity.HasIndex(e => e.ModelCode);
        });

        // NOTE: DispatchJob implements the Transactional Outbox pattern for reliable, non-blocking EBS-50 delivery.
        modelBuilder.Entity<DispatchJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MacAddress).IsRequired().HasMaxLength(16);
            entity.Property(e => e.MachineNo).HasMaxLength(50);
            entity.Property(e => e.ModelCode).HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.OverrideThemeColor).HasMaxLength(20);
            entity.Property(e => e.LastError).HasMaxLength(1000);

            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            entity.HasIndex(e => new { e.MacAddress, e.DesiredRevision });
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(50);
            entity.Property(e => e.Value).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(200);
        });
    }
}

