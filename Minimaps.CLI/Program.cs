using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minimaps.CLI.Commands;
using System.CommandLine;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConfiguration(configuration.GetSection("Logging"));
    builder.AddConsole();
});

var rootCommand = new RootCommand("Minimaps.CLI")
{
    GenerateCommand.Create(configuration, loggerFactory, cts.Token),
    MigrateCommand.Create(configuration, loggerFactory, cts.Token)
};
return await rootCommand.Parse(args).InvokeAsync();
