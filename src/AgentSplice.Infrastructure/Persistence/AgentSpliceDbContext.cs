using AgentSplice.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// The local metadata store (docs/SPECIFICATION.md FR-DATA-002, FR-DATA-003).
/// </summary>
/// <remarks>
/// The model is deliberately provider-neutral. No column declares a SQLite type name, no property
/// uses a provider-specific value generator, and nothing here issues raw SQL. SQLite is the only
/// provider shipped in Stage 1C, but FR-DATA-003 commits to PostgreSQL through the same contracts,
/// and a model that has to be rewritten to honour that commitment is not a contract.
///
/// Every entity is append-only in practice: the write path inserts and the retention sweep deletes.
/// Nothing updates a row, because evidence that could be revised after the fact would stop being
/// evidence.
/// </remarks>
public sealed class AgentSpliceDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    public AgentSpliceDbContext(DbContextOptions<AgentSpliceDbContext> options)
        : base(options)
    {
    }

    internal DbSet<ExchangeRow> Exchanges => Set<ExchangeRow>();

    internal DbSet<ExchangeObservationRow> Observations => Set<ExchangeObservationRow>();

    internal DbSet<ExchangeMeasurementRow> Measurements => Set<ExchangeMeasurementRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ExchangeRow>(entity =>
        {
            entity.ToTable("exchanges");
            entity.HasKey(row => row.ExchangeId);

            entity.Property(row => row.ExchangeId).ValueGeneratedNever();
            entity.Property(row => row.PublicRequestId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.TraceId).HasMaxLength(32);
            entity.Property(row => row.ClientModelId).HasMaxLength(256);
            entity.Property(row => row.RuntimeEndpointId).HasMaxLength(128);
            entity.Property(row => row.UpstreamModelId).HasMaxLength(256);
            entity.Property(row => row.ResolutionAliasId).HasMaxLength(256);
            entity.Property(row => row.ErrorCode).HasMaxLength(128);
            entity.Property(row => row.EnvironmentSnapshotId).HasMaxLength(128);
            entity.Property(row => row.UpstreamMediaType).HasMaxLength(128);
            entity.Property(row => row.UpstreamRequestId).HasMaxLength(128);

            // The list is ordered by (startedAt DESC, exchangeId DESC) and the retention sweep deletes
            // by the same leading column, so one index serves both (FR-TRACE-009, FR-DATA-007).
            entity.HasIndex(row => new { row.StartedAtTicks, row.ExchangeId });

            // The two documented list filters.
            entity.HasIndex(row => row.Status);
            entity.HasIndex(row => row.RuntimeEndpointId);
        });

        modelBuilder.Entity<ExchangeObservationRow>(entity =>
        {
            entity.ToTable("exchange_observations");
            entity.HasKey(row => row.ObservationId);
            entity.Property(row => row.ObservationId).ValueGeneratedNever();

            // Sequence order is the timeline's contract, so it is also the index.
            entity.HasIndex(row => new { row.ExchangeId, row.Sequence }).IsUnique();

            entity.HasOne(row => row.Exchange)
                .WithMany(exchange => exchange.Observations)
                .HasForeignKey(row => row.ExchangeId)

                // Cascade rather than an application-side sweep of each child table: a retention
                // deletion that removed an exchange and left its timeline behind would leave rows no
                // API can reach and no policy will ever expire (FR-DATA-008).
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExchangeMeasurementRow>(entity =>
        {
            entity.ToTable("exchange_measurements");
            entity.HasKey(row => row.MeasurementId);
            entity.Property(row => row.MeasurementId).ValueGeneratedNever();
            entity.Property(row => row.Name).HasMaxLength(128).IsRequired();

            entity.HasIndex(row => row.ExchangeId);

            entity.HasOne(row => row.Exchange)
                .WithMany(exchange => exchange.Measurements)
                .HasForeignKey(row => row.ExchangeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
