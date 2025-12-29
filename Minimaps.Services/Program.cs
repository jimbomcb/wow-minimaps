using Minimaps.Database;
using Minimaps.Services;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using Minimaps.Shared.RibbitClient;
using Minimaps.Shared.TileStores;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddMinimapsDatabase();

builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var logger = serviceProvider.GetRequiredService<ILogger<WebhookEventLog>>();
    return new WebhookEventLog(configuration.GetValue<string>("Services:EventWebhook"), logger);
});

builder.Services.AddSingleton<IRibbitClient>(new RibbitClient(RibbitRegion.US));
builder.Services.AddSingleton<IListFileService, ListFileService>();

builder.Services.AddHostedService<EventLoggerService>();
builder.Services.AddHostedService<ScanMapsService>();
builder.Services.AddHostedService<ProductDiscoveryService>();

builder.Services.AddSingleton<ResourceLocService>();
builder.Services.AddSingleton<BlizztrackFSService>();

var tileStoreProvider = builder.Configuration["TileStoreProvider"];
if (string.Equals(tileStoreProvider, "R2", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ITileStore, R2TileStore>();
}
else
{
    builder.Services.AddSingleton<ITileStore, LocalTileStore>();
}

var host = builder.Build();
await host.RunAsync();