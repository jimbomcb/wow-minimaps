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

		RootCommand rootCommand = new("Minimaps.Generator");
		rootCommand.Options.Add(productOpt);
		rootCommand.Options.Add(cascRegionOpt);
		rootCommand.SetAction(async args =>
		{
			using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
			ILogger logger = factory.CreateLogger("Generator");

			await new Generator(cts.Token, logger, args.GetValue(productOpt)!, args.GetValue(cascRegionOpt)!).Generate();
			return 0;
		});
		return await rootCommand.Parse(args).InvokeAsync();
	}
}