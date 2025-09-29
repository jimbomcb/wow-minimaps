using Microsoft.Extensions.Hosting;
using Minimaps.Database.Tables;
using Minimaps.Database.TypeHandlers;
using Npgsql;

namespace Minimaps.Database;

public static class HostBuilderExtensions
{
    public static TBuilder AddMinimapsDatabase<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.AddNpgsqlDataSource("minimaps-database", configureDataSourceBuilder: x =>
        {
            x.UseNodaTime();
            x.AddTypeInfoResolverFactory(new NpgsqlTypeResolverFactory()); // BuildVersion type handling
            x.MapEnum<ScanState>();
        });
        return builder;
    }
}
