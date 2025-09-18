using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Diagnostics;
using Minimaps.Generator.Database;

namespace Minimaps.Generator;

internal class Program
{
	static async Task<int> Main(string[] args)
	{
		var cts = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, eventArgs) =>
		{
			eventArgs.Cancel = true;
			cts.Cancel();
		};

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

		var connectionStringOpt = new Option<string>("--connection-string")
		{
			Description = "PostgreSQL connection string for database operations",
			Required = false
		};

		RootCommand rootCommand = new("Minimaps.CLI");

		// TODO: Combine command logging factory setup

		// Minimap generation 
		// (not great, mainly just me learning how they structure the CASC data and how to pull from CDNs)
		var generateCommand = new Command("generate", "Generate minimap data from CASC")
		{
			productOpt,
			cascRegionOpt,
			filterId,
			additionalCdnOpt
		};
		generateCommand.SetAction(async args =>
		{
			using ILoggerFactory factory = LoggerFactory.Create(builder =>
			{
				builder.AddConsole();
				//builder.SetMinimumLevel(LogLevel.Trace);
			});
			ILogger logger = factory.CreateLogger("Generator");

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
		var migrateCommand = new Command("migrate", "Run database migrations")
		{
			connectionStringOpt
		};
		migrateCommand.SetAction(async parseResult =>
		{
			// todo: combine config/logging etc loading
			var configuration = new ConfigurationBuilder()
				.AddEnvironmentVariables()
				.AddJsonFile("appsettings.json", optional: true)
				.AddJsonFile("appsettings.Development.json", optional: true)
				.Build();

			using var loggerFactory = LoggerFactory.Create(builder =>
			{
				builder.AddConsole();
			});
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
	}
}