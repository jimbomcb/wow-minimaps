using Minimaps.Shared;
using Minimaps.Shared.Types;
using Npgsql.Internal;
using Npgsql.Internal.Postgres;

namespace Minimaps.Database.TypeHandlers;

#pragma warning disable NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

public class NpgsqlTypeResolverFactory : PgTypeInfoResolverFactory
{
    public override IPgTypeInfoResolver CreateResolver() => new Resolver();
    public override IPgTypeInfoResolver? CreateArrayResolver() => new ArrayResolver();
    protected static DataTypeName ContentHashDataType => new("pg_catalog.bytea");
    protected static DataTypeName BuildVersionDataType => new("pg_catalog.int8");

    private class Resolver : IPgTypeInfoResolver
    {

        TypeInfoMappingCollection? _mappings;
        protected TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new());

        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => Mappings.Find(type, dataTypeName, options);

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddStructType<ContentHash>(ContentHashDataType,
                static (options, mapping, _) => mapping.CreateInfo(options, new NpgsqlContentHashConverter()));
            mappings.AddStructType<BuildVersion>(BuildVersionDataType,
                static (options, mapping, _) => mapping.CreateInfo(options, new NpgsqlBuildVersionConverter()));
            // TODO: MinimapComposition mapping? 
            return mappings;
        }
    }

    private class ArrayResolver : Resolver, IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        new TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new(base.Mappings));

        public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => Mappings.Find(type, dataTypeName, options);

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddStructArrayType<ContentHash>(ContentHashDataType);
            mappings.AddStructArrayType<BuildVersion>(BuildVersionDataType);
            return mappings;
        }
    }
}


#pragma warning restore NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
