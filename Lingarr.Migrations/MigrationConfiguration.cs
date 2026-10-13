using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Lingarr.Migrations;

public static class MigrationConfiguration
{
    /// <summary>
    /// Configures FluentMigrator services for the SQLite database.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    public static IServiceCollection AddFluentMigrator(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddFluentMigratorCore()
            .ConfigureRunner(migrationRunnerBuilder => migrationRunnerBuilder
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .WithVersionTable(new CustomVersionTableMetaData())
                .ScanIn(typeof(MigrationConfiguration).Assembly).For.Migrations())
            .AddLogging(migrationRunnerBuilder => migrationRunnerBuilder.AddFluentMigratorConsole());

        return services;
    }

    /// <summary>
    /// Runs all pending migrations. SQLite is the only supported engine; legacy
    /// MySQL/PostgreSQL databases cannot be upgraded in place and must be rebuilt.
    /// </summary>
    /// <param name="serviceProvider">The service provider containing the migration runner.</param>
    public static void RunMigrations(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }
}
