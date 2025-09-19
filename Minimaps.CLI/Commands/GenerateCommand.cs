using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Diagnostics;

namespace Minimaps.CLI.Commands;

public static class GenerateCommand
{
    public static Command Create(IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var command = new Command("generate", "Generate minimap data from CASC");

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

        command.Add(productOpt);
        command.Add(cascRegionOpt);
        command.Add(filterId);
        command.Add(additionalCdnOpt);

        command.SetAction(async args =>
        {
            var logger = loggerFactory.CreateLogger("Generator");

            var timer = Stopwatch.StartNew();

            var generator = new Generator.Generator(new Generator.GeneratorConfig
            {
                Product = args.GetValue(productOpt)!,
                CascRegion = args.GetValue(cascRegionOpt)!,
                FilterId = args.GetValue(filterId)!,
                AdditionalCDNs = [.. args.GetValue(additionalCdnOpt)!]
            }, logger, cancellationToken);

            await generator.Generate();

            logger.LogInformation("Generator finished in {Elapsed}ms", timer.ElapsedMilliseconds);
            return 0;
        });

        return command;
    }
}