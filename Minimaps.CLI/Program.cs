using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minimaps.CLI.Commands;
using System.CommandLine;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true);

builder.AddServiceDefaults();
builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var rootCommand = new RootCommand("Minimaps.CLI")
{
    GenerateCommand.Create(builder.Configuration, loggerFactory, cts.Token),
    MigrateCommand.Create(builder.Configuration, loggerFactory, cts.Token),
    ExploreAdtCommand.Create(builder.Configuration, loggerFactory, cts.Token),
    SyncTilesCommand.Create(builder.Configuration, loggerFactory, cts.Token),
};

return await rootCommand.Parse(args).InvokeAsync();