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
            x.EnableDynamicJson();
            x.UseNodaTime();
#pragma warning disable NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            x.AddTypeInfoResolverFactory(new NpgsqlTypeResolverFactory());
#pragma warning restore NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            x.MapEnum<ScanState>();
        });
        return builder;
    }
}
