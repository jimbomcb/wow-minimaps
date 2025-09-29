using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Minimaps.CLI;

public class DatabaseMigrationService(string connectionString, ILogger<DatabaseMigrationService>? logger = null)
{
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    private readonly ILogger<DatabaseMigrationService>? _logger = logger;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Migrate(), cancellationToken);
    }

    public void Migrate()
    {
        _logger?.LogInformation("Starting database migration...");

        var serviceProvider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_connectionString)
                .ScanIn(Assembly.GetAssembly(typeof(Database.Migrations.InitialSchema))).For.Migrations())
            .AddLogging(lb => lb
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider(validateScopes: false);

        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        try
        {
            _logger?.LogInformation("Executing database migrations...");
            runner.MigrateUp();

            if (runner.HasMigrationsToApplyUp())
                throw new Exception("Still migrations to apply");

            _logger?.LogInformation("Database migration completed successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Database migration failed :(");
            throw;
        }
    }
}