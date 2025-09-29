using Minimaps.Shared;
using Minimaps.Shared.Types;
using Npgsql.Internal;
using Npgsql.Internal.Postgres;

namespace Minimaps.Database.TypeHandlers;

#pragma warning disable NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

public class NpgsqlTypeResolverFactory : PgTypeInfoResolverFactory
{
    public override IPgTypeInfoResolver? CreateArrayResolver() => new ArrayResolver();
    public override IPgTypeInfoResolver CreateResolver() => new Resolver();

    private class Resolver : IPgTypeInfoResolver
    {
        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
        {
            if (type == typeof(BuildVersion))
                return new(options, new NpgsqlBuildVersionConverter(), new(DataTypeName.FromDisplayName("int8")));

            // Minimap compositions get serialized as JSONB objects of {"0,5": "hash", "12,34": "hash"}
            if (type == typeof(MinimapComposition))
                //return new(options, new NpgsqlMinimapCompositionConverter(), new(DataTypeName.FromDisplayName("jsonb")));
                throw new NotImplementedException("Please don't pass compositions directly to Npgsql, serialize to string");


            return null;
        }
    }

    private class ArrayResolver : IPgTypeInfoResolver
    {
        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
        {
            // TODO: Validation
            if (type == typeof(BuildVersion[]) || type == typeof(List<BuildVersion>))
            {
                return new PgTypeInfo(
                    options,
                    new NpgsqlBuildVersionConverter(),
                    new(DataTypeName.FromDisplayName("int8[]"))
                );
            }

            return null;
        }
    }
}

#pragma warning restore NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
