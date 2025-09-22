using Minimaps.Web.API.TileStores;

namespace Minimaps.Web.API;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddControllers();
        builder.Services.AddSingleton<DapperContext>();
        builder.Services.AddSingleton<ITileStore, LocalTileStore>();

        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.Run();
    }
}
