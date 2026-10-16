using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// P2 polish batch: the dedicated video-hash column and the FTS5 title index.
///
/// <para>
/// <c>video_hash</c> (movies + episodes): the OpenSubtitles moviehash of the
/// media file, lazily computed and written back by
/// <c>LazyMediaHasher.WriteBackVideoHashAsync</c>. Until now that write-back
/// targeted <c>media_hash</c> — a column the translation pipeline already
/// owns (its subtitle-set hash, with clear-on-change events) — which forced a
/// strict <c>IS NULL</c> guard that made the cached value near-inert, since
/// fresh EF-inserted rows carry the empty-string sentinel instead of NULL.
/// The dedicated column resolves the ownership conflict: <c>media_hash</c>
/// returns wholly to the translation pipeline, <c>video_hash</c> has exactly
/// one writer, and the NULL guard survives only to keep two concurrent lazy
/// computes from racing each other.
/// </para>
/// <para>
/// <c>media_title_fts</c>: a regular (self-storing) FTS5 virtual table over
/// media titles (scale audit P1-4 — the library search's
/// <c>lower(title) LIKE '%…%'</c> can use no B-tree and degenerates into a
/// full-table scan at the 100k-rows scale). Rows are keyed by a rowid that
/// encodes the media type (movie = 1e12 + id, show = 2e12 + id,
/// episode = 3e12 + id) so the three independent id sequences never collide;
/// <c>media_type</c> is stored UNINDEXED for operator inspection. The index
/// is deliberately NOT populated here: existing libraries are indexed by the
/// lazy drift self-heal on the first title search (see TitleFts in
/// Lingarr.Core), and incremental maintenance happens through the context's
/// save hook. SQLitePCLraw's bundled e_sqlite3 has compiled FTS5 in since
/// 3.9 (bundle 2.1.x ships 3.44+), so no build flag is needed.
/// </para>
/// </summary>
[Migration(11)]
public class M0011_VideoHashAndTitleSearchFts : Migration
{
    public override void Up()
    {
        // Dedicated column for the lazily computed OpenSubtitles moviehash.
        // Nullable with no default: NULL means "not computed yet" and is the
        // only state the write-back fills.
        Alter.Table("movies")
            .AddColumn("video_hash").AsCustom("TEXT").Nullable();
        Alter.Table("episodes")
            .AddColumn("video_hash").AsCustom("TEXT").Nullable();

        // Virtual tables have no FluentMigrator fluent surface; raw DDL is
        // the M0007/M0008 precedent. Dropping the virtual table later drops
        // its five shadow tables with it.
        Execute.Sql(
            "CREATE VIRTUAL TABLE media_title_fts USING fts5(title, media_type UNINDEXED)");
    }

    public override void Down()
    {
        // Removes the virtual table and its shadow tables (verified: DROP
        // TABLE on an fts5 vtab cascades to the %_data/%_idx/%_content/
        // %_docsize/%_config entries in sqlite_master).
        Execute.Sql("DROP TABLE IF EXISTS media_title_fts");

        Delete.Column("video_hash").FromTable("movies");
        Delete.Column("video_hash").FromTable("episodes");
    }
}
