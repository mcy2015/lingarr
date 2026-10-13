using Microsoft.EntityFrameworkCore;

namespace Lingarr.Core.Configuration;

/// <summary>
/// SQLite is the single supported database engine. The connection string is
/// derived from the SQLITE_DB_PATH environment variable: an absolute path is
/// used as-is, a relative path is resolved against /app/config (the persistent
/// config volume in the container image).
/// </summary>
public static class DatabaseConfiguration
{
    private const string DefaultSqliteDbFileName = "lingarr.db";

    /// <summary>
    /// Gets the SQLite connection string for the application database.
    /// </summary>
    public static string GetConnectionString()
    {
        var sqliteDbPath = Environment.GetEnvironmentVariable("SQLITE_DB_PATH") ?? DefaultSqliteDbFileName;
        if (Path.IsPathRooted(sqliteDbPath))
        {
            return $"Data Source={sqliteDbPath};Foreign Keys=True";
        }

        return $"Data Source=/app/config/{sqliteDbPath};Foreign Keys=True";
    }

    /// <summary>
    /// Configures the DbContext options for SQLite (WAL journal mode, busy
    /// timeout and relaxed synchronous writes are applied per connection by
    /// <see cref="SqlitePragmaConnectionInterceptor"/>).
    /// </summary>
    public static void ConfigureDbContext(DbContextOptionsBuilder options)
    {
        var connectionString = GetConnectionString();

        options.UseSqlite(connectionString,
                sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            .AddInterceptors(new SqlitePragmaConnectionInterceptor())
            .UseSnakeCaseNamingConvention();
    }
}
