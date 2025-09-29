using Minimaps.Shared.Types;
using Npgsql.Internal;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Minimaps.Database.TypeHandlers;

#pragma warning disable NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

/// <summary>
/// Based on the Npgsql text converters
/// Not using it for now, I don't quite trust it as it was requiring text mode when the base json serializers don't use it?
/// </summary>
public class NpgsqlMinimapCompositionConverter : PgStreamingConverter<MinimapComposition>
{
    private readonly Encoding CompositionEncoding = Encoding.ASCII;
    public override MinimapComposition Read(PgReader reader)
        => Read(async: false, reader, CompositionEncoding).GetAwaiter().GetResult();

    public override ValueTask<MinimapComposition> ReadAsync(PgReader reader, CancellationToken cancellationToken = default)
        => Read(async: true, reader, CompositionEncoding, cancellationToken);

    public override Size GetSize(SizeContext context, MinimapComposition value, ref object? writeState)
        => CompositionEncoding.GetByteCount(ConvertTo(value).Span);

    public override void Write(PgWriter writer, MinimapComposition value)
        => writer.WriteChars(ConvertTo(value).Span, CompositionEncoding);

    public override ValueTask WriteAsync(PgWriter writer, MinimapComposition value, CancellationToken cancellationToken = default)
        => writer.WriteCharsAsync(ConvertTo(value), CompositionEncoding, cancellationToken);

    public override bool CanConvert(DataFormat format, out BufferRequirements bufferRequirements)
    {
        bufferRequirements = BufferRequirements.None;
        return format is DataFormat.Text;
    }

    private ReadOnlyMemory<char> ConvertTo(MinimapComposition value) => JsonSerializer.Serialize(value).AsMemory();

    private MinimapComposition ConvertFrom(string value)
        => JsonSerializer.Deserialize<MinimapComposition>(value)
            ?? throw new InvalidOperationException("Failed to deserialize MinimapComposition");

    ValueTask<MinimapComposition> Read(bool async, PgReader reader, Encoding encoding, CancellationToken cancellationToken = default)
    {
        return async
            ? ReadAsync(reader, encoding, cancellationToken)
            : new(ConvertFrom(encoding.GetString(reader.ReadBytes(reader.CurrentRemaining))));

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        async ValueTask<MinimapComposition> ReadAsync(PgReader reader, Encoding encoding, CancellationToken cancellationToken)
            => ConvertFrom(encoding.GetString(await reader.ReadBytesAsync(reader.CurrentRemaining, cancellationToken).ConfigureAwait(false)));
    }
}

#pragma warning restore NPG9001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
