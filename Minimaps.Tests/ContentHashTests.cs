using Minimaps.Shared.Types;
using System.Buffers;
using System.Security.Cryptography;

namespace Minimaps.Tests;

public class ContentHashTests
{
    [Fact]
    public void Constructor_FromHexString_ValidInput()
    {
        var hex = "1234567890abcdef1234567890abcdef";
        var hash = new ContentHash(hex);

        Assert.Equal(hex, hash.ToHex());
    }

    [Fact]
    public void Constructor_FromHexString_UpperCase()
    {
        var hex = "1234567890ABCDEF1234567890ABCDEF";
        var hash = new ContentHash(hex);

        Assert.Equal("1234567890abcdef1234567890abcdef", hash.ToHex());
    }

    [Fact]
    public void Constructor_FromHexString_InvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ContentHash("1234567890abcdef"));
        Assert.Throws<ArgumentException>(() => new ContentHash("1234567890abcdef1234567890abcdef12"));
        Assert.Throws<ArgumentException>(() => new ContentHash(""));
    }

    [Fact]
    public void Constructor_FromHexString_InvalidCharacters_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ContentHash("1234567890abcdef1234567890abcdeG"));
        Assert.Throws<ArgumentException>(() => new ContentHash("1234567890abcdef1234567890abcd!@"));
    }

    [Fact]
    public void Constructor_FromBytes_ValidInput()
    {
        var bytes = new byte[16] { 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef, 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef };
        var hash = new ContentHash(bytes);

        Assert.Equal("1234567890abcdef1234567890abcdef", hash.ToHex());
    }

    [Fact]
    public void Constructor_FromBytes_InvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ContentHash(new byte[15]));
        Assert.Throws<ArgumentException>(() => new ContentHash(new byte[17]));
        Assert.Throws<ArgumentException>(() => new ContentHash([]));
    }

    [Fact]
    public void CopyTo_ValidDestination()
    {
        var hash = new ContentHash("1234567890abcdef1234567890abcdef");
        var destination = new byte[16];

        hash.CopyTo(destination);

        var expected = new byte[16] { 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef, 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef };
        Assert.Equal(expected, destination);
    }

    [Fact]
    public void CopyTo_DestinationTooSmall_Throws()
    {
        var hash = new ContentHash("1234567890abcdef1234567890abcdef");
        var destination = new byte[15];

        Assert.Throws<ArgumentException>(() => hash.CopyTo(destination));
    }

    [Fact]
    public void FromStream_ValidStream()
    {
        var data = "Hello, World!"u8.ToArray();
        using var stream = new MemoryStream(data);

        var hash = ContentHash.FromStream(stream);

        var expectedHash = MD5.HashData(data);
        var expectedHex = Convert.ToHexStringLower(expectedHash);

        Assert.Equal(expectedHex, hash.ToHex());
    }

    [Fact]
    public void FromStream_EmptyStream()
    {
        using var stream = new MemoryStream();

        var hash = ContentHash.FromStream(stream);

        Assert.NotNull(hash.ToHex());
        Assert.Equal(32, hash.ToHex().Length);
    }

    [Fact]
    public void Equality_SameHash()
    {
        var hash1 = new ContentHash("1234567890abcdef1234567890abcdef");
        var hash2 = new ContentHash("1234567890abcdef1234567890abcdef");

        Assert.Equal(hash1, hash2);
        Assert.True(hash1 == hash2);
        Assert.False(hash1 != hash2);
        Assert.True(hash1.Equals(hash2));
        Assert.Equal(hash1.GetHashCode(), hash2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentHash()
    {
        var hash1 = new ContentHash("1234567890abcdef1234567890abcdef");
        var hash2 = new ContentHash("abcdef1234567890abcdef1234567890");

        Assert.NotEqual(hash1, hash2);
        Assert.False(hash1 == hash2);
        Assert.True(hash1 != hash2);
        Assert.False(hash1.Equals(hash2));
    }

    [Fact]
    public void Equality_WithObject()
    {
        var hash = new ContentHash("1234567890abcdef1234567890abcdef");
        object other = new ContentHash("1234567890abcdef1234567890abcdef");
        object different = new ContentHash("abcdef1234567890abcdef1234567890");

        Assert.True(hash.Equals(other));
        Assert.False(hash.Equals(different));
        Assert.False(hash.Equals("not a hash"));
        Assert.False(hash.Equals(null));
    }

    [Fact]
    public void ToString_ReturnsHex()
    {
        var hex = "1234567890abcdef1234567890abcdef";
        var hash = new ContentHash(hex);

        Assert.Equal(hex, hash.ToString());
    }

    [Fact]
    public void ToHex_ReturnsLowercase()
    {
        var hash = new ContentHash("1234567890ABCDEF1234567890ABCDEF");

        Assert.Equal("1234567890abcdef1234567890abcdef", hash.ToHex());
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    [InlineData("1234567890abcdef1234567890abcdef")]
    [InlineData("abcdef1234567890abcdef1234567890")]
    public void RoundTrip_HexToHashToHex(string originalHex)
    {
        var hash = new ContentHash(originalHex.ToLowerInvariant());
        var resultHex = hash.ToHex();

        Assert.Equal(originalHex.ToLowerInvariant(), resultHex);
    }

    [Fact]
    public void RoundTrip_BytesToHashToBytes()
    {
        var originalBytes = new byte[16] { 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef, 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef };
        var hash = new ContentHash(originalBytes);
        var resultBytes = new byte[16];
        hash.CopyTo(resultBytes);

        Assert.Equal(originalBytes, resultBytes);
    }


    [Fact]
    public void Constructor_FromReadOnlySequence_SingleSegment()
    {
        var bytes = new byte[16] { 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef, 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef };
        var sequence = new ReadOnlySequence<byte>(bytes);

        var hash = new ContentHash(sequence);

        Assert.Equal("1234567890abcdef1234567890abcdef", hash.ToHex());
    }

    [Fact]
    public void Constructor_FromReadOnlySequence_MultipleSegments()
    {
        var bytes1 = new byte[8] { 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef };
        var bytes2 = new byte[8] { 0x12, 0x34, 0x56, 0x78, 0x90, 0xab, 0xcd, 0xef };

        var segment1 = new TestSegment(bytes1);
        var segment2 = new TestSegment(bytes2, segment1);
        var sequence = new ReadOnlySequence<byte>(segment1, 0, segment2, 8);

        var hash = new ContentHash(sequence);

        Assert.Equal("1234567890abcdef1234567890abcdef", hash.ToHex());
    }

    private class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory, TestSegment? previous = null)
        {
            Memory = memory;
            if (previous != null)
            {
                previous.Next = this;
                RunningIndex = previous.RunningIndex + previous.Memory.Length;
            }
        }
    }
}