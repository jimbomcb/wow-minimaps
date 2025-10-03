using Minimaps.Database;
using Minimaps.Frontend.Components;
using Minimaps.Shared.TileStores;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddMinimapsDatabase();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

builder.Services.AddSingleton<ITileStore, LocalTileStore>();


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
