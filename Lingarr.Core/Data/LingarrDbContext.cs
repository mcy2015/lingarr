using Lingarr.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Lingarr.Core.Entities;

namespace Lingarr.Core.Data;

public class LingarrDbContext : DbContext
{
    public DbSet<Movie> Movies { get; set; }
    public DbSet<Show> Shows { get; set; }
    public DbSet<Season> Seasons { get; set; }
    public DbSet<Episode> Episodes { get; set; }
    public DbSet<Image> Images { get; set; }
    public DbSet<Setting> Settings { get; set; }
    public DbSet<TranslationRequest> TranslationRequests { get; set; }
    public DbSet<TranslationGlossary> TranslationGlossaries { get; set; }
    public DbSet<TranslationRequestEvent> TranslationRequestEvents { get; set; }
    public DbSet<TranslationRequestLine> TranslationRequestLines { get; set; }
    public DbSet<PathMapping> PathMappings { get; set; }
    public DbSet<Statistics> Statistics { get; set; }
    public DbSet<DailyStatistics> DailyStatistics { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<AuthSession> AuthSessions { get; set; }
    public DbSet<MediaSubtitleTrack> MediaSubtitleTracks { get; set; }
    public DbSet<SubtitleLanguageCache> SubtitleLanguageCaches { get; set; }
    public DbSet<SubtitleSearchState> SubtitleSearchStates { get; set; }
    public DbSet<SubtitleDownload> SubtitleDownloads { get; set; }
    public DbSet<SubtitleSearchBlacklist> SubtitleSearchBlacklists { get; set; }
    public DbSet<SubtitleSearchSession> SubtitleSearchSessions { get; set; }
    public DbSet<ProviderQuotaUsage> ProviderQuotaUsages { get; set; }
    public DbSet<CoverageDaily> CoverageDailies { get; set; }

    public LingarrDbContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new MovieConfiguration());
        modelBuilder.ApplyConfiguration(new ShowConfiguration());
        modelBuilder.ApplyConfiguration(new SeasonConfiguration());
        modelBuilder.ApplyConfiguration(new EpisodeConfiguration());
        modelBuilder.ApplyConfiguration(new ImageConfiguration());
        modelBuilder.ApplyConfiguration(new MediaSubtitleTrackConfiguration());

        // Audit P1-7: the sniff-persistence table name is fixed by migration
        // M0007 (singular, path-keyed) — pinned here so EnsureCreated-built
        // test schemas match the FluentMigrator schema exactly.
        modelBuilder.Entity<SubtitleLanguageCache>(builder =>
        {
            builder.ToTable("subtitle_language_cache");
        });

        // Subtitle-search tables (M2 batch of the M0008/M0009 migrations):
        // table names are pinned the same way so EnsureCreated-built test
        // schemas match the FluentMigrator schema exactly. The two counter
        // tables were created with raw DDL (composite inline PKs, SQLite)
        // and additionally declare their key order here — multiple [Key]
        // attributes alone leave the ordering to reflection order, which is
        // not a contract.
        modelBuilder.Entity<SubtitleSearchState>(builder =>
        {
            builder.ToTable("subtitle_search_state");
        });
        modelBuilder.Entity<SubtitleDownload>(builder =>
        {
            builder.ToTable("subtitle_downloads");
        });
        modelBuilder.Entity<SubtitleSearchBlacklist>(builder =>
        {
            builder.ToTable("subtitle_search_blacklist");
        });
        modelBuilder.Entity<SubtitleSearchSession>(builder =>
        {
            builder.ToTable("subtitle_search_sessions");
        });
        modelBuilder.Entity<ProviderQuotaUsage>(builder =>
        {
            builder.ToTable("provider_quota_usage");
            builder.HasKey(usage => new { usage.Date, usage.Provider });
        });
        modelBuilder.Entity<CoverageDaily>(builder =>
        {
            builder.ToTable("coverage_daily");
            builder.HasKey(snapshot => new { snapshot.Date, snapshot.MediaType, snapshot.Language });
        });

        ConvertTimestampsToUtc(modelBuilder);
    }

    private static void ConvertTimestampsToUtc(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified),
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

        var nullableConverter = new ValueConverter<DateTimeOffset?, DateTime?>(
            value => value.HasValue
                ? DateTime.SpecifyKind(value.Value.UtcDateTime, DateTimeKind.Unspecified)
                : null,
            value => value.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
                : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType != typeof(DateTimeOffset) && property.ClrType != typeof(DateTimeOffset?))
                {
                    continue;
                }

                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(converter);
                }
                else
                {
                    property.SetValueConverter(nullableConverter);
                }
            }
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entities = ChangeTracker.Entries()
            .Where(x => x.Entity is BaseEntity && (x.State == EntityState.Added || x.State == EntityState.Modified));

        foreach (var entity in entities)
        {
            if (entity.State == EntityState.Added)
            {
                ((BaseEntity)entity.Entity).CreatedAt = DateTimeOffset.UtcNow;
            }

            ((BaseEntity)entity.Entity).UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}