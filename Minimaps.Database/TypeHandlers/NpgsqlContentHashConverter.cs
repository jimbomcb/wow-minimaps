using Minimaps.Shared.Types;
using Npgsql.Internal;

namespace Minimaps.Database.TypeHandlers;

#pragma warning disable NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

/// <summary>
/// ContentHash's MD5 bytes (16) are stored in BYTEAs
/// </summary>
public class NpgsqlContentHashConverter : PgStreamingConverter<ContentHash>
{
    public override ContentHash Read(PgReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        reader.Read(bytes);
        return new(bytes);
    }

    public override async ValueTask<ContentHash> ReadAsync(PgReader reader, CancellationToken cancellationToken = default)
    {
        var byteSequence = await reader.ReadBytesAsync(16, cancellationToken);
        return new ContentHash(byteSequence);
    }

    public override void Write(PgWriter writer, ContentHash value)
    {
        writer.WriteChars(value.ToHex().AsSpan(), System.Text.Encoding.ASCII);
    }

    public override ValueTask WriteAsync(PgWriter writer, ContentHash value, CancellationToken cancellationToken = default)
    {
        var bytes = new byte[16];
        value.CopyTo(bytes);
        return writer.WriteBytesAsync(bytes, cancellationToken);
    }

    public override Size GetSize(SizeContext context, ContentHash value, ref object? writeState)
    {
        return Size.Create(16); // BYTEA 16 bytes
    }
}

#pragma warning restore NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
