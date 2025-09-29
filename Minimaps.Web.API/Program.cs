using Microsoft.AspNetCore.Server.Kestrel.Core;
using Minimaps.Database.TypeHandlers;
using Minimaps.Shared.TileStores;
using Minimaps.Database;

namespace Minimaps.Web.API;

public class Program
{
    public static void Main(string[] args)
    {
        DapperTypeHandler.RegisterTypeHandlers();

        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddMinimapsDatabase();

        builder.Services.AddControllers();
        builder.Services.AddSingleton<ITileStore, LocalTileStore>();
        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // 1MB
        });

        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.Run();
    }
}
