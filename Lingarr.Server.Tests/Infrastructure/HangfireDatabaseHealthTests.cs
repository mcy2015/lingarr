using System;
using System.IO;
using System.Linq;
using System.Text;
using Lingarr.Server.Modules.Infrastructure;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// NAS incident 2026-10-19, round two: the lock storm left Hangfire.db
/// with "database disk image is malformed"; every enqueue endpoint died
/// until a human deleted the file. Boot now quarantines a corrupt jobs
/// database — recurring jobs re-register and pending translation requests
/// re-enqueue from the main database, so a fresh file is always safe.
/// </summary>
public class HangfireDatabaseHealthTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-hangfire-health-" + Guid.NewGuid().ToString("N"));

    public HangfireDatabaseHealthTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    private string CreateHealthyDatabase()
    {
        var path = Path.Combine(_root, "Hangfire.db");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE marker (id INTEGER PRIMARY KEY); INSERT INTO marker VALUES (1);";
        command.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void HealthyDatabase_IsLeftUntouched()
    {
        var path = CreateHealthyDatabase();
        var before = File.GetLastWriteTimeUtc(path);

        var quarantined = InfrastructureModule.QuarantineCorruptHangfireDatabase(path);

        Assert.False(quarantined);
        Assert.True(File.Exists(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void MalformedDatabase_IsQuarantinedWithAllSidecars()
    {
        var path = Path.Combine(_root, "Hangfire.db");
        // garbage bytes: not a SQLite file at all
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is definitely not a sqlite database"));
        File.WriteAllText(path + "-wal", "stale wal");
        File.WriteAllText(path + "-shm", "stale shm");

        var quarantined = InfrastructureModule.QuarantineCorruptHangfireDatabase(path);

        Assert.True(quarantined);
        // the original path is gone (Hangfire creates a fresh one) and one
        // timestamped quarantine copy per file exists alongside it
        Assert.False(File.Exists(path));
        var quarantinedFiles = Directory.GetFiles(_root, "Hangfire.db*.corrupt-*");
        Assert.Equal(3, quarantinedFiles.Length);
    }

    [Fact]
    public void MissingDatabase_IsNotAnError()
    {
        var quarantined =
            InfrastructureModule.QuarantineCorruptHangfireDatabase(Path.Combine(_root, "absent.db"));

        Assert.False(quarantined);
    }

    [Fact]
    public void LogicallyBrokenButReadableDatabase_IsQuarantined()
    {
        // a real SQLite file whose quick_check does not answer "ok": cut a
        // healthy database short mid-page
        var path = CreateHealthyDatabase();
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.Take(bytes.Length / 2).ToArray());

        var quarantined = InfrastructureModule.QuarantineCorruptHangfireDatabase(path);

        Assert.True(quarantined);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_root, "Hangfire.db.corrupt-*"));
    }
}
