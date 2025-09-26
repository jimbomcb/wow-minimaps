using Microsoft.AspNetCore.Server.Kestrel.Core;
using Minimaps.Database.TypeHandlers;
using Minimaps.Web.API.TileStores;

namespace Minimaps.Web.API;

public class Program
{
    public static void Main(string[] args)
    {
        DapperTypeHandler.RegisterTypeHandlers();

        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddNpgsqlDataSource("minimaps-database", configureDataSourceBuilder: x =>
        {
            x.AddTypeInfoResolverFactory(new NpgsqlTypeResolverFactory()); // BuildVersion type handling
        });


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
