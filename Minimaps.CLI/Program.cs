using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minimaps.Generator;
using Minimaps.Generator.Database;
using System.CommandLine;
using System.Diagnostics;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    //builder.SetMinimumLevel(LogLevel.Trace);
});

RootCommand rootCommand = new("Minimaps.CLI");

// Minimap generation 
// (not great, mainly just me learning how they structure the CASC data and how to pull from CDNs)
var generateCommand = new Command("generate", "Generate minimap data from CASC");

var productOpt = new Option<string>("--product")
{
    Description = "CASC Product",
    DefaultValueFactory = (_) => "wow",
    Required = true
};

var cascRegionOpt = new Option<string>("--casc-region")
{
    Description = "CASC Region",
    DefaultValueFactory = (_) => "us",
    Required = true
};

var filterId = new Option<string>("--filter-id")
{
    Description = "Map ID filtering (* supported)",
    DefaultValueFactory = (_) => "*",
    Required = true
};

var additionalCdnOpt = new Option<string[]>("--additional-cdn")
{
    Description = "Additional CDN URLs to use for downloading files",
    DefaultValueFactory = (_) => Array.Empty<string>(),
    Required = false,
    AllowMultipleArgumentsPerToken = true
};

generateCommand.Add(productOpt);
generateCommand.Add(cascRegionOpt);
generateCommand.Add(filterId);
generateCommand.Add(additionalCdnOpt);
generateCommand.SetAction(async args =>
{
    ILogger logger = loggerFactory.CreateLogger("Generator");

    var timer = Stopwatch.StartNew();

    var generator = new Generator(new()
    {
        Product = args.GetValue(productOpt)!,
        CascRegion = args.GetValue(cascRegionOpt)!,
        FilterId = args.GetValue(filterId)!,
        AdditionalCDNs = [.. args.GetValue(additionalCdnOpt)!]
    }, logger, cts.Token);
    await generator.Generate();

    logger.LogInformation("Generator finished in {Elapsed}ms", timer.ElapsedMilliseconds);
    return 0;
});

// minimaps.cli migrate
var migrateCommand = new Command("migrate", "Run database migrations");

var connectionStringOpt = new Option<string>("--connection-string")
{
    Description = "PostgreSQL connection string for database operations",
    Required = false
};

migrateCommand.Add(connectionStringOpt);
migrateCommand.SetAction(async parseResult =>
{
    var logger = loggerFactory.CreateLogger<DatabaseMigrationService>();

    var connectionString = parseResult.GetValue(connectionStringOpt);
    if (string.IsNullOrEmpty(connectionString))
    {
        connectionString = configuration.GetConnectionString("minimaps-database"); // aspire provided
        if (string.IsNullOrEmpty(connectionString))
        {
            logger.LogInformation("Available connection strings:");
            var connectionStringsSection = configuration.GetSection("ConnectionStrings");
            foreach (var child in connectionStringsSection.GetChildren())
            {
                logger.LogInformation("  {Key}={Value}", child.Key, child.Value?.Substring(0, Math.Min(40, child.Value.Length)) + "...");
            }

            logger.LogError("Connection string is required. Provide --connection-string (or minimaps-database env var via aspire)");
            return 1;
        }
    }

    try
    {
        logger.LogInformation("Running database migrations.....");

        var migrationService = new DatabaseMigrationService(connectionString, logger);
        await migrationService.MigrateAsync(cts.Token);
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Migration failed");
        return 1;
    }
});

rootCommand.Add(generateCommand);
rootCommand.Add(migrateCommand);

return await rootCommand.Parse(args).InvokeAsync();
