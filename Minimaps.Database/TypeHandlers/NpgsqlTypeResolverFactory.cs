using Minimaps.Shared;
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
            {
                return new PgTypeInfo(
                      options,
                      new NpgsqlBuildVersionConverter(),
                      new(DataTypeName.FromDisplayName("int8"))
                  );
            }

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

public class NpgsqlBuildVersionConverter : PgStreamingConverter<BuildVersion>
{
    public override BuildVersion Read(PgReader reader)
    {
        var longValue = reader.ReadInt64();
        return (BuildVersion)longValue;
    }

    public override ValueTask<BuildVersion> ReadAsync(PgReader reader, CancellationToken cancellationToken = default)
    {
        var longValue = reader.ReadInt64();
        return new ValueTask<BuildVersion>((BuildVersion)longValue);
    }

    public override void Write(PgWriter writer, BuildVersion value)
    {
        writer.WriteInt64((long)value);
    }

    public override ValueTask WriteAsync(PgWriter writer, BuildVersion value, CancellationToken cancellationToken = default)
    {
        writer.WriteInt64((long)value);
        return ValueTask.CompletedTask;
    }

    public override Size GetSize(SizeContext context, BuildVersion value, ref object? writeState)
    {
        return Size.Create(8); // bigint 8 bytes
    }
}

#pragma warning restore NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
