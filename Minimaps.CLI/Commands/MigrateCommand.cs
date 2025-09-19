using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Minimaps.CLI.Commands;

public static class MigrateCommand
{
    public static Command Create(IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var command = new Command("migrate", "Run database migrations");

        var connectionStringOpt = new Option<string>("--connection-string")
        {
            Description = "PostgreSQL connection string for database operations",
            Required = false
        };

        command.Add(connectionStringOpt);

        command.SetAction(async parseResult =>
        {
            var logger = loggerFactory.CreateLogger<DatabaseMigrationService>();

            var connectionString = parseResult.GetValue(connectionStringOpt);
            if (string.IsNullOrEmpty(connectionString))
            {
                connectionString = configuration.GetConnectionString("minimaps-database"); // aspire provided
                if (string.IsNullOrEmpty(connectionString))
                {
                    logger.LogError("Connection string is required. Provide --connection-string (or minimaps-database env var via aspire)");
                    return 1;
                }
            }

            try
            {
                logger.LogInformation("Running database migrations.....");

                var migrationService = new DatabaseMigrationService(connectionString, logger);
                await migrationService.MigrateAsync(cancellationToken);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Migration failed");
                return 1;
            }
        });

        return command;
    }
}