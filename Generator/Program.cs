using Microsoft.Extensions.Logging;
using System.CodeDom.Compiler;
using System.CommandLine;

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
			using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
			ILogger logger = factory.CreateLogger("Generator");

			var generator = new Generator(new Generator.Config
			{
				Product = args.GetValue(productOpt)!,
				CascRegion = args.GetValue(cascRegionOpt)!,
				FilterId = args.GetValue(filterId)!,
			}, logger, cts.Token);

			await generator.Generate();
			return 0;
		});
		return await rootCommand.Parse(args).InvokeAsync();
	}
}