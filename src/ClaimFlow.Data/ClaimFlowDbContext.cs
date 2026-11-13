using ClaimFlow.Data.Entities;
using ClaimFlow.Shared;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Data;

public sealed class ClaimFlowDbContext : DbContext
{
    public ClaimFlowDbContext(DbContextOptions<ClaimFlowDbContext> options)
        : base(options)
    {
    }

    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Payer> Payers => Set<Payer>();

    public DbSet<ClaimStatusHistory> ClaimStatusHistory => Set<ClaimStatusHistory>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureClaims(modelBuilder);
        ConfigureLines(modelBuilder);
        ConfigurePatients(modelBuilder);
        ConfigurePayers(modelBuilder);
        ConfigureStatusHistory(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureProcessedEvents(modelBuilder);
    }

    private static void ConfigureClaims(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Claim>();
        entity.ToTable("Claims");
        entity.HasKey(c => c.Id);
        entity.Property(c => c.ClaimId).HasMaxLength(64).IsRequired();
        entity.Property(c => c.PayerId).HasMaxLength(32).IsRequired();
        entity.Property(c => c.MemberId).HasMaxLength(64).IsRequired();
        entity.Property(c => c.ProviderNpi).HasMaxLength(10).IsRequired();
        entity.Property(c => c.PlaceOfService).HasMaxLength(2).IsRequired();
        entity.Property(c => c.CorrelationId).HasMaxLength(64).IsRequired();
        entity.Property(c => c.ValidationRuleCodes).HasMaxLength(256).IsRequired();
        entity.Property(c => c.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(24);
        entity.Property(c => c.DenialReasonCode).HasConversion<string>().HasMaxLength(16);
        entity.Property(c => c.TotalChargeCents).HasColumnType("int");
        entity.Property(c => c.AllowedCents).HasColumnType("int");
        entity.Property(c => c.PaidCents).HasColumnType("int");
        entity.Property(c => c.PatientResponsibilityCents).HasColumnType("int");
        entity.HasIndex(c => c.ClaimId).HasDatabaseName("IX_Claims_ClaimId");
        entity.HasIndex(c => c.Status).HasDatabaseName("IX_Claims_Status");
        entity.HasIndex(c => c.MemberId).HasDatabaseName("IX_Claims_MemberId");
        entity.HasIndex(c => new { c.PayerId, c.ServiceDate }).HasDatabaseName("IX_Claims_Payer_ServiceDate");
        entity.HasIndex(c => new { c.ServiceDate, c.Status }).HasDatabaseName("IX_Claims_ServiceDate_Status");
        entity.HasIndex(c => new { c.PayerId, c.DenialReasonCode }).HasDatabaseName("IX_Claims_Payer_Denial");
        entity.HasIndex(c => new { c.ReceivedAtUnixMs, c.Status }).HasDatabaseName("IX_Claims_Received_Status");
        entity.HasIndex(c => new { c.PayerId, c.MemberId, c.ServiceDate, c.TotalChargeCents })
            .IsUnique()
            .HasFilter("[Status] <> 'Rejected'")
            .HasDatabaseName("UX_Claims_NaturalKey");
        entity.HasOne(c => c.Patient)
            .WithMany()
            .HasForeignKey(c => c.PatientId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureLines(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ClaimLine>();
        entity.ToTable("ClaimLines");
        entity.HasKey(l => l.Id);
        entity.Property(l => l.CptCode).HasMaxLength(10).IsRequired();
        entity.Property(l => l.Icd10Code).HasMaxLength(10).IsRequired();
        entity.Property(l => l.PlaceOfService).HasMaxLength(2).IsRequired();
        entity.Property(l => l.ChargeCents).HasColumnType("int");
        entity.Property(l => l.AllowedCents).HasColumnType("int");
        entity.Property(l => l.PaidCents).HasColumnType("int");
        entity.Property(l => l.PatientResponsibilityCents).HasColumnType("int");
        entity.HasIndex(l => l.ClaimId).HasDatabaseName("IX_ClaimLines_ClaimId");
        entity.HasIndex(l => l.CptCode).HasDatabaseName("IX_ClaimLines_CptCode");
        entity.HasOne(l => l.Claim)
            .WithMany(c => c.Lines)
            .HasForeignKey(l => l.ClaimId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigurePatients(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Patient>();
        entity.ToTable("Patients");
        entity.HasKey(p => p.Id);
        entity.Property(p => p.MemberId).HasMaxLength(64).IsRequired();
        entity.Property(p => p.GivenName).HasMaxLength(100).IsRequired();
        entity.Property(p => p.FamilyName).HasMaxLength(100).IsRequired();
        entity.Property(p => p.SsnLast4).HasMaxLength(4).IsRequired();
        entity.Property(p => p.SsnHash).HasMaxLength(64).IsRequired();
        entity.Property(p => p.AddressLine1).HasMaxLength(200).IsRequired();
        entity.Property(p => p.City).HasMaxLength(100).IsRequired();
        entity.Property(p => p.State).HasMaxLength(2).IsRequired();
        entity.Property(p => p.PostalCode).HasMaxLength(10).IsRequired();
        entity.HasIndex(p => p.MemberId)
            .IsUnique()
            .HasFilter("[MemberId] <> ''")
            .HasDatabaseName("UX_Patients_MemberId");
    }

    private static void ConfigurePayers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Payer>();
        entity.ToTable("Payers");
        entity.HasKey(p => p.PayerId);
        entity.Property(p => p.PayerId).HasMaxLength(32);
        entity.Property(p => p.Name).HasMaxLength(100).IsRequired();
        entity.HasData(PayerDirectory.All.Select(p => new Payer
        {
            PayerId = p.PayerId,
            Name = p.Name,
            FilingLimitDays = p.FilingLimitDays,
            FeeSchedulePercent = p.FeeSchedulePercent,
            CoveragePercent = p.CoveragePercent,
        }));
    }

    private static void ConfigureStatusHistory(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ClaimStatusHistory>();
        entity.ToTable("ClaimStatusHistory");
        entity.HasKey(h => h.Id);
        entity.Property(h => h.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(h => h.Reason).HasMaxLength(512).IsRequired();
        entity.Property(h => h.Actor).HasMaxLength(64).IsRequired();
        entity.Property(h => h.CorrelationId).HasMaxLength(64).IsRequired();
        entity.HasIndex(h => new { h.ClaimId, h.Sequence }).HasDatabaseName("IX_StatusHistory_Claim_Sequence");
        entity.HasOne(h => h.Claim)
            .WithMany(c => c.StatusHistory)
            .HasForeignKey(h => h.ClaimId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureOutbox(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<OutboxMessage>();
        entity.ToTable("OutboxMessages");
        entity.HasKey(o => o.Id);
        entity.Property(o => o.EventType).HasMaxLength(64).IsRequired();
        entity.Property(o => o.Payload).IsRequired();
        entity.Property(o => o.CorrelationId).HasMaxLength(64).IsRequired();
        entity.Property(o => o.LastError).HasMaxLength(2000);
        entity.HasIndex(o => new { o.ProcessedAt, o.OccurredAt }).HasDatabaseName("IX_Outbox_Pending");
    }

    private static void ConfigureProcessedEvents(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcessedEvent>();
        entity.ToTable("ProcessedEvents");
        entity.HasKey(e => e.EventId);
        entity.Property(e => e.ClaimId).HasMaxLength(64).IsRequired();
        entity.Property(e => e.Consumer).HasMaxLength(64).IsRequired();
        entity.HasIndex(e => e.ClaimId).HasDatabaseName("IX_ProcessedEvents_ClaimId");
    }
}
