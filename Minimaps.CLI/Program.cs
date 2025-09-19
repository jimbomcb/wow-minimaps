using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minimaps.CLI.Commands;
using System.CommandLine;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);
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
    MigrateCommand.Create(builder.Configuration, loggerFactory, cts.Token)
};

return await rootCommand.Parse(args).InvokeAsync();