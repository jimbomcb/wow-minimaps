using Minimaps.Database;
using Minimaps.Frontend.Components;
using Minimaps.Shared.TileStores;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddMinimapsDatabase();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

var tileStoreProvider = builder.Configuration["TileStoreProvider"];
if (string.Equals(tileStoreProvider, "R2", StringComparison.OrdinalIgnoreCase))
{
    //builder.Services.AddSingleton<ITileStore, R2TileStore>(); // Not necessary, served direct from tile store CDN 
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

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
