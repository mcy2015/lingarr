using System.Data;
using System.Data.Common;
using Lingarr.Core.Configuration;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

    /// <summary>
    /// Title writes captured by <see cref="SavingChanges"/> for the FTS index
    /// (see <see cref="TitleFts"/>): entity references, because ids of Added
    /// rows only exist after the flush. Cleared again in
    /// <see cref="SavedChanges"/>.
    /// </summary>
    private List<(EntityEntry Entry, bool Delete)>? _pendingTitleFtsWrites;

    public LingarrDbContext(DbContextOptions options) : base(options)
    {
        // Scale audit P1-4: media titles are mirrored into the media_title_fts
        // index as they are saved. Centralizing this in the context — rather
        // than in the three *arr sync classes — is deliberate: EpisodeSync
        // writes titles purely through tracked entities without holding a
        // DbContext itself, and ids of new rows only exist after SaveChanges;
        // the SavingChanges/SavedChanges pair is the single point where both
        // facts are known for every writer (sync, webhook, manual edits).
        SavingChanges += OnSavingChanges;
        SavedChanges += OnSavedChanges;
    }

    private void OnSavingChanges(object? sender, SavingChangesEventArgs eventArgs)
    {
        _pendingTitleFtsWrites = TitleFts.CollectPendingWrites(ChangeTracker);
    }

    private void OnSavedChanges(object? sender, SavedChangesEventArgs eventArgs)
    {
        var pending = _pendingTitleFtsWrites;
        _pendingTitleFtsWrites = null;
        if (pending is not { Count: > 0 })
        {
            return;
        }

        try
        {
            TitleFts.ApplyPendingWrites(this, pending);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // The title index is a search accelerator with a lazy self-heal
            // on the next search (TitleFts rebuilds on drift); an index
            // failure must never fail the save that carries the real data.
            // No logger is available on the context itself — the swallow is
            // deliberate and bounded by the drift rebuild.
        }
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
        // the FluentMigrator schema is the source of truth; this mirror pins
        // what EnsureCreated-built schemas (test fixtures) need for parity —
        // table names for all six tables, and the indexes raw SQL addresses
        // by database name (the dequeue/upsert/boost trio on the queue, the
        // three history indexes, the two session indexes). Not mirrored:
        // column types/defaults (migrations own those; EF models them
        // close enough for SQLite) and the partial WHERE clause of the boost
        // index (see below). The two counter tables were created with raw
        // DDL (composite inline PKs, SQLite) and additionally declare their
        // key order here — multiple [Key] attributes alone leave the
        // ordering to reflection order, which is not a contract.
        modelBuilder.Entity<SubtitleSearchState>(builder =>
        {
            builder.ToTable("subtitle_search_state");
            // The dequeue covering index and the upsert target (M0008): raw
            // SQL in the queue service addresses both by database name, so
            // EnsureCreated-built test schemas must carry them too.
            builder.HasIndex(state => new { state.Status, state.PriorityRank, state.NextAttemptAt })
                .HasDatabaseName("ix_subtitle_search_state_dequeue");
            builder.HasIndex(state => new { state.MediaType, state.MediaId })
                .IsUnique()
                .HasDatabaseName("ux_subtitle_search_state_media");
            // M0008 also creates this one as a PARTIAL index
            // (WHERE webhook_boost_until IS NOT NULL). HasFilter takes a raw
            // SQL string, and SQLite validates the filter's column reference
            // at CREATE INDEX time — a fixed "webhook_boost_until" literal
            // only resolves when the model carries the snake-case naming
            // convention, which several EnsureCreated contexts do not. The
            // mirror therefore declares the same key under the same name
            // without the WHERE clause; the production database keeps the
            // partial form from the migration.
            builder.HasIndex(state => state.WebhookBoostUntil)
                .HasDatabaseName("ix_subtitle_search_state_boost");
        });
        modelBuilder.Entity<SubtitleDownload>(builder =>
        {
            builder.ToTable("subtitle_downloads");
            // M0008: subtitle-card refresh for one media item, history-screen
            // keyset pagination (created_at > cursor), history filters
            // (source x result).
            builder.HasIndex(download => new { download.MediaType, download.MediaId, download.CreatedAt })
                .HasDatabaseName("ix_subtitle_downloads_media");
            builder.HasIndex(download => download.CreatedAt)
                .HasDatabaseName("ix_subtitle_downloads_created");
            builder.HasIndex(download => new { download.Source, download.Result, download.CreatedAt })
                .HasDatabaseName("ix_subtitle_downloads_source");
        });
        modelBuilder.Entity<SubtitleSearchBlacklist>(builder =>
        {
            builder.ToTable("subtitle_search_blacklist");
        });
        modelBuilder.Entity<SubtitleSearchSession>(builder =>
        {
            builder.ToTable("subtitle_search_sessions");
            // M0009: per-media session lookup and the created-at cursor.
            builder.HasIndex(session => new { session.MediaType, session.MediaId, session.CreatedAt })
                .HasDatabaseName("ix_subtitle_search_sessions_media");
            builder.HasIndex(session => session.CreatedAt)
                .HasDatabaseName("ix_subtitle_search_sessions_created");
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

/// <summary>
/// The FTS5 title index behind library search (scale audit P1-4, migration
/// M0011): <c>media_title_fts</c> replaces <c>lower(title) LIKE '%…%'</c>,
/// which can use no B-tree and scans the whole table per search.
///
/// <para>
/// <b>Shape.</b> A regular (self-storing) FTS5 virtual table over
/// <c>(title, media_type UNINDEXED)</c>. The rowid encodes the media type —
/// movie = <see cref="MovieRowIdBase"/> + id, show = <see cref="ShowRowIdBase"/>
/// + id, episode = <see cref="EpisodeRowIdBase"/> + id — because the three
/// tables run independent id sequences that would otherwise collide inside
/// the index. The bases are far beyond any int32 id, so decoding is just a
/// range check.
/// </para>
///
/// <para>
/// <b>Maintenance</b> (the locked strategy): no SQL triggers — they would
/// entangle the EF/migration chain for no gain. Instead (a) incremental
/// upserts ride along every save via the context's SavingChanges/SavedChanges
/// hook (<see cref="CollectPendingWrites"/>/<see cref="ApplyPendingWrites"/>),
/// covering add/rename/delete of Movie/Show/Episode for every writer, and
/// (b) searches lazily self-heal drift: when the indexed row count no longer
/// matches the library count (a lost write, ExecuteDelete-based removals that
/// bypass the change tracker, or a library that predates M0011) the index is
/// rebuilt in one pass before matching. A 100k-row rebuild is a one-off
/// C-level scan, acceptable per the batch spec. Set-based ExecuteDelete
/// removals leaving orphan rows are additionally harmless by construction:
/// the id set from the index is always intersected with live table rows.
/// </para>
///
/// <para>
/// <b>Query semantics.</b> ASCII queries match on word prefixes
/// (<c>"star"*</c> hits <c>Star Trek</c> and <c>Starship Troopers</c>, not
/// <c>Startled Muse</c>'s infix) — the semantics used by bazarr-class
/// library searches, and the reason mid-word fragments no longer match.
/// Non-ASCII (CJK) queries never reach this index: the unicode61 tokenizer
/// does not segment hanzi runs (a whole run folds into one token), so prefix
/// matching would silently miss; those queries keep the legacy LIKE
/// substring predicate in the caller.
/// </para>
/// </summary>
public static class TitleFts
{
    /// <summary>The FTS5 virtual table created by migration M0011.</summary>
    public const string TableName = "media_title_fts";

    /// <summary>Provider name of the only relational provider in play.</summary>
    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    private const long MovieRowIdBase = 1_000_000_000_000L;
    private const long ShowRowIdBase = 2_000_000_000_000L;
    private const long EpisodeRowIdBase = 3_000_000_000_000L;

    /// <summary>2^31 — every possible int32 media id fits one range.</summary>
    private const long RowIdRangeWidth = 2_147_483_648L;

    /// <summary>
    /// Resolves an ASCII search term against the index: returns the matching
    /// movie and show ids, or null when the index cannot serve the query
    /// (non-SQLite provider such as the InMemory test provider, table absent
    /// — e.g. an EnsureCreated-built test schema — or any SQLite error), in
    /// which case the caller falls back to the LIKE predicate. Drift
    /// self-heal runs first, so the id sets reflect the current library.
    /// </summary>
    public static async Task<Dictionary<MediaType, List<int>>?> TrySearchAsync(
        DbContext context,
        string needle,
        CancellationToken cancellationToken = default)
    {
        if (!IsSqlite(context))
        {
            return null;
        }

        try
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!TableExists(connection))
            {
                return null;
            }

            await RebuildIfDriftedAsync(connection, cancellationToken).ConfigureAwait(false);

            var match = BuildMatchExpression(needle);
            if (match is null)
            {
                return null;
            }

            return new Dictionary<MediaType, List<int>>
            {
                [MediaType.Movie] = await QueryMatchingIdsAsync(
                    connection, MediaType.Movie, match, cancellationToken).ConfigureAwait(false),
                [MediaType.Show] = await QueryMatchingIdsAsync(
                    connection, MediaType.Show, match, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (SqliteException)
        {
            // The index is an accelerator; the LIKE predicate in the caller
            // stays the safety net (e.g. a concurrent rebuild racing this
            // search surfaces here and costs one LIKE query, nothing more).
            return null;
        }
    }

    /// <summary>
    /// Builds the FTS5 MATCH expression for a search term: every
    /// whitespace-separated token becomes a quoted prefix phrase, AND-ed
    /// together. Quoting neutralizes FTS5 query syntax inside user input
    /// ("and", parentheses, quotes); the trailing <c>*</c> is the prefix
    /// form. Returns null when the term has no tokens at all — the caller
    /// falls back to LIKE rather than inventing a match-nothing query.
    /// </summary>
    public static string? BuildMatchExpression(string needle)
    {
        var tokens = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0
            ? null
            : string.Join(" AND ", tokens.Select(token => "\"" + token.Replace("\"", "\"\"") + "\"*"));
    }

    /// <summary>
    /// Captures the media-title writes of the pending save: Added rows,
    /// Modified rows whose Title actually changed (unchanged titles — e.g. a
    /// statistics-scan flag flip — never re-index), and Deleted rows.
    /// </summary>
    public static List<(EntityEntry Entry, bool Delete)>? CollectPendingWrites(ChangeTracker changeTracker)
    {
        List<(EntityEntry Entry, bool Delete)>? pending = null;
        foreach (var entry in changeTracker.Entries())
        {
            if (entry.Entity is not (Movie or Show or Episode))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                case EntityState.Modified when entry.Property(TitleProperty).IsModified:
                    (pending ??= []).Add((entry, Delete: false));
                    break;
                case EntityState.Deleted:
                    (pending ??= []).Add((entry, Delete: true));
                    break;
            }
        }

        return pending;
    }

    /// <summary>
    /// Pushes the captured writes into the index after the save flushed (ids
    /// exist by then). Raw ADO on the context's connection — outside the EF
    /// pipeline so the hook cannot re-enter SaveChanges — joining the user's
    /// transaction when one is active. Skips silently when the index does
    /// not exist (non-migrated test schemas); the drift rebuild heals any
    /// write that ever fails.
    /// </summary>
    public static void ApplyPendingWrites(
        DbContext context,
        IReadOnlyList<(EntityEntry Entry, bool Delete)> pending)
    {
        if (!IsSqlite(context) || pending.Count == 0)
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            connection.Open();
        }

        try
        {
            if (!TableExists(connection))
            {
                return;
            }

            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (entry, delete) in pending)
            {
                if (MediaIdentity(entry.Entity) is not { } identity)
                {
                    continue;
                }

                var rowId = RowIdFor(identity.Type, identity.Id);
                var title = delete ? null : entry.Property(TitleProperty).CurrentValue as string;
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                if (title is null)
                {
                    command.CommandText = $"DELETE FROM {TableName} WHERE rowid = $rowid";
                    AddParameter(command, "$rowid", rowId);
                }
                else
                {
                    // Upsert: drop any stale row for this rowid (a recycled id
                    // after an orphaned delete) before inserting the new title.
                    command.CommandText =
                        $"DELETE FROM {TableName} WHERE rowid = $rowid; " +
                        $"INSERT INTO {TableName}(rowid, title, media_type) VALUES ($rowid, $title, $type)";
                    AddParameter(command, "$rowid", rowId);
                    AddParameter(command, "$title", title);
                    AddParameter(command, "$type", MediaTypeName(identity.Type));
                }

                command.ExecuteNonQuery();
            }
        }
        finally
        {
            if (openedHere)
            {
                connection.Close();
            }
        }
    }

    private const string TitleProperty = "Title";

    private static bool IsSqlite(DbContext context) =>
        string.Equals(context.Database.ProviderName, SqliteProviderName, StringComparison.Ordinal);

    private static long RowIdFor(MediaType mediaType, int id) => BaseFor(mediaType) + id;

    private static long BaseFor(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => MovieRowIdBase,
        MediaType.Show => ShowRowIdBase,
        _ => EpisodeRowIdBase
    };

    private static (long Low, long High) RowIdRange(MediaType mediaType)
    {
        var basis = BaseFor(mediaType);
        return (basis, basis + RowIdRangeWidth);
    }

    private static string MediaTypeName(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => "movie",
        MediaType.Show => "show",
        _ => "episode"
    };

    private static (MediaType Type, int Id)? MediaIdentity(object entity) => entity switch
    {
        Movie movie => (MediaType.Movie, movie.Id),
        Show show => (MediaType.Show, show.Id),
        Episode episode => (MediaType.Episode, episode.Id),
        _ => null
    };

    private static bool TableExists(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{TableName}'";
        return Convert.ToInt64(command.ExecuteScalar()) == 1L;
    }

    /// <summary>
    /// The lazy self-heal: total indexed rows must equal the total media rows
    /// (movies + shows + episodes). Any difference — empty index over a
    /// populated library (the upgrade path into M0011), rows lost to an
    /// ExecuteDelete removal, or a write that failed mid-save — triggers one
    /// full rebuild. A same-count-but-wrong-rows state (e.g. one orphan and
    /// one missing row cancelling out) is not caught; only crash windows can
    /// produce it, and the next rename or rebuild of the affected row heals
    /// it. The comparison costs one indexed count scan per search — linear
    /// C-level work, still orders below the full-table lower()/LIKE scan the
    /// index exists to avoid.
    /// </summary>
    private static async Task RebuildIfDriftedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        long indexed;
        long media;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT (SELECT COUNT(*) FROM media_title_fts), " +
                "((SELECT COUNT(*) FROM movies) + (SELECT COUNT(*) FROM shows) + (SELECT COUNT(*) FROM episodes))";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            indexed = reader.GetInt64(0);
            media = reader.GetInt64(1);
        }

        if (indexed == media)
        {
            return;
        }

        using var rebuild = connection.CreateCommand();
        rebuild.CommandText =
            $"DELETE FROM {TableName}; " +
            $"INSERT INTO {TableName}(rowid, title, media_type) " +
            $"SELECT {MovieRowIdBase} + id, title, 'movie' FROM movies; " +
            $"INSERT INTO {TableName}(rowid, title, media_type) " +
            $"SELECT {ShowRowIdBase} + id, title, 'show' FROM shows; " +
            $"INSERT INTO {TableName}(rowid, title, media_type) " +
            $"SELECT {EpisodeRowIdBase} + id, title, 'episode' FROM episodes";
        await rebuild.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<int>> QueryMatchingIdsAsync(
        DbConnection connection,
        MediaType mediaType,
        string match,
        CancellationToken cancellationToken)
    {
        var (low, high) = RowIdRange(mediaType);
        var basis = BaseFor(mediaType);
        var ids = new List<int>();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT rowid FROM {TableName} " +
            $"WHERE {TableName} MATCH $match AND rowid >= $low AND rowid < $high";
        AddParameter(command, "$match", match);
        AddParameter(command, "$low", low);
        AddParameter(command, "$high", high);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add((int)(reader.GetInt64(0) - basis));
        }

        return ids;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}