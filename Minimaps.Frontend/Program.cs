using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Minimaps.Database;
using Minimaps.Frontend.Components;
using Minimaps.Shared.TileStores;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddMinimapsDatabase();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

var tileStoreProvider = builder.Configuration["TileStoreProvider"];
if (string.Equals(tileStoreProvider, "R2", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ITileStore, R2TileStore>(); // Not used (yet), served direct from tile store CDN.
                                                              // Might store some proxied static data in the future. 
}
else
{
    builder.Services.AddSingleton<ITileStore, LocalTileStore>();
}

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseResponseCompression();
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();