using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Unique content identifier, MD5 hash of byte stream, used frequently for keying, deduplication
/// Stored internally as 2x int64 (no heap alloc), 16 bytes BYTEA in Postgres, and json etc as lowercase hex string.
/// </summary>
[JsonConverter(typeof(ContentHashConverter))]
public readonly struct ContentHash : IEquatable<ContentHash>
{
    private readonly ulong _part1;
    private readonly ulong _part2;

    public ContentHash(string hex)
    {
        if (hex.Length != 32)
            throw new ArgumentException("Must be 32 hex characters", nameof(hex));

        Span<byte> bytes = stackalloc byte[16];
        Convert.FromHexString(hex, bytes, out int chars, out int bytesWritten);
        if (chars != 32 || bytesWritten != 16)
            throw new ArgumentException("Invalid hex content hash " + hex, nameof(hex));

        _part1 = BitConverter.ToUInt64(bytes[..8]);
        _part2 = BitConverter.ToUInt64(bytes[8..]);
    }

    public ContentHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("Must be 16 bytes", nameof(bytes));

        _part1 = BitConverter.ToUInt64(bytes[..8]);
        _part2 = BitConverter.ToUInt64(bytes[8..]);
    }

    public ContentHash(ReadOnlySequence<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("Must be 16 bytes", nameof(bytes));

        Span<byte> temp = stackalloc byte[16];
        if (bytes.IsSingleSegment)
        {
            _part1 = BitConverter.ToUInt64(bytes.FirstSpan);
            _part2 = BitConverter.ToUInt64(bytes.FirstSpan.Slice(8));
        }
        else
        {
            // slow path
            bytes.CopyTo(temp);
            _part1 = BitConverter.ToUInt64(temp);
            _part2 = BitConverter.ToUInt64(temp.Slice(8));
        }
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("Destination too small");
        BitConverter.TryWriteBytes(destination, _part1);
        BitConverter.TryWriteBytes(destination[8..], _part2);
    }

    public string ToHex()
    {
        Span<byte> bytes = stackalloc byte[16];
        CopyTo(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static ContentHash FromStream(Stream stream)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(stream, hash);
        return new(hash);
    }

    public static bool TryParse(string versiohash, out ContentHash result)
    {
        try
        {
            result = new ContentHash(versiohash);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }


    public bool Equals(ContentHash other) => _part1 == other._part1 && _part2 == other._part2;
    public override bool Equals(object? obj) => obj is ContentHash hash && Equals(hash);
    public override int GetHashCode() => HashCode.Combine(_part1, _part2);
    public override string ToString() => ToHex();

    public static bool operator ==(ContentHash left, ContentHash right) => left.Equals(right);
    public static bool operator !=(ContentHash left, ContentHash right) => !(left == right);
}