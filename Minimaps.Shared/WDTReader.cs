using System.Text;

namespace Minimaps.Shared;

public readonly record struct MinimapTileData(int X, int Y, uint FileId);

/// <summary>
/// FDID fields from a MAID entry (https://wowdev.wiki/WDT#MAID_chunk)
/// </summary>
public readonly record struct MAIDEntry(
    uint RootADT,
    uint Obj0ADT,
    uint Obj1ADT,
    uint Tex0ADT,
    uint LodADT,
    uint MapTexture,
    uint MapTextureN,
    uint MinimapTexture
);

public class WDTReader : IDisposable
{
    public readonly record struct ChunkHeader(string Ident, uint Size);
    private readonly Stream _baseStream;
    private readonly BinaryReader _reader;
    private bool _disposed;

    public WDTReader(Stream baseStream)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _reader = new BinaryReader(_baseStream);
    }

    /// <summary>
    /// Read the full MAID chunk with all FDID fields per tile.
    /// </summary>
    public Dictionary<(int X, int Y), MAIDEntry>? ReadMAID()
    {
        _baseStream.Position = 0;

        while (_baseStream.Position < _baseStream.Length)
        {
            var header = ReadChunkHeader();
            if (header.Ident?.Length != 4)
                throw new InvalidDataException($"Invalid chunk ident: {header.Ident ?? "null"}");

            if (header.Ident == "MAID")
            {
                // MAID chunk contains minimap texture IDs (& more)
                // https://wowdev.wiki/WDT#MAID_chunk
                // 64x64 grid of entries, each entry is 7x uint32 where the last uint32 is the texture ID

                var entries = new Dictionary<(int X, int Y), MAIDEntry>();
                for (int row = 0; row < 64; row++)
                {
                    for (int col = 0; col < 64; col++)
                    {
                        var entry = new MAIDEntry(
                            _reader.ReadUInt32(), // rootADT
                            _reader.ReadUInt32(), // obj0ADT
                            _reader.ReadUInt32(), // obj1ADT
                            _reader.ReadUInt32(), // tex0ADT
                            _reader.ReadUInt32(), // lodADT
                            _reader.ReadUInt32(), // mapTexture
                            _reader.ReadUInt32(), // mapTextureN
                            _reader.ReadUInt32()  // minimapTexture
                        );

                        if (entry != default)
                            entries[(col, row)] = entry;
                    }
                }

                return entries;
            }

            // Skip this chunk
            _baseStream.Position += header.Size;
        }

        return null;
    }

    public List<MinimapTileData>? ReadMinimapTiles()
    {
        var maid = ReadMAID();
        if (maid == null) return null;

        var tiles = new List<MinimapTileData>();
        foreach (var (coord, entry) in maid)
        {
            if (entry.MinimapTexture > 0)
                tiles.Add(new(coord.X, coord.Y, entry.MinimapTexture));
        }
        return tiles;
    }

    private ChunkHeader ReadChunkHeader()
    {
        var identBytes = _reader.ReadBytes(4);
        Array.Reverse(identBytes); // Reverse for correct endianness
        var size = _reader.ReadUInt32();
        var ident = Encoding.UTF8.GetString(identBytes);

        return new ChunkHeader(ident, size);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reader?.Dispose();
            _baseStream?.Dispose();
            _disposed = true;
        }
    }
}