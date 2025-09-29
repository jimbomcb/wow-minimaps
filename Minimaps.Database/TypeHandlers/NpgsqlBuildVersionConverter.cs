using Minimaps.Shared;
using Npgsql.Internal;

namespace Minimaps.Database.TypeHandlers;

#pragma warning disable NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

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
