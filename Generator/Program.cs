using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Diagnostics;

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

		RootCommand rootCommand = new("Minimaps.Generator");
		rootCommand.Options.Add(productOpt);
		rootCommand.Options.Add(cascRegionOpt);
		rootCommand.Options.Add(filterId);
		rootCommand.SetAction(async args =>
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
			}, logger, cts.Token);
			await generator.Generate();

			logger.LogInformation("Generator finished in {Elapsed}ms", timer.ElapsedMilliseconds);
			return 0;
		});
		return await rootCommand.Parse(args).InvokeAsync();
	}
}