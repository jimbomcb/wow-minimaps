using System.Text;

namespace Minimaps.Shared;

public readonly record struct MinimapTileData(int X, int Y, uint FileId);

public class WDTReader : IDisposable
{
    private readonly Stream _baseStream;
    private readonly BinaryReader _reader;
    private bool _disposed;

    public WDTReader(Stream baseStream)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _reader = new BinaryReader(_baseStream);
    }

    public List<MinimapTileData> ReadMinimapTiles()
    {
        // TODO: See how we can go about validating version, historical WDTs

        var tiles = new List<MinimapTileData>();
        _baseStream.Position = 0;

        // Parse chunked structure: int32 token, int32 size, byte[size]
        while (_baseStream.Position < _baseStream.Length)
        {
            var header = ReadChunkHeader();
            if (header.Ident?.Length != 4)
                throw new InvalidDataException($"Invalid chunk ident: {header.Ident ?? "null"}");

            if (header.Ident == "MAID")
            {
                // MAID chunk contains minimap texture IDs
                // https://wowdev.wiki/WDT#MAID_chunk
                // 64x64 grid of entries, each entry is 7x uint32 where the last uint32 is the texture ID
                for (int row = 0; row < 64; row++)
                {
                    for (int col = 0; col < 64; col++)
                    {
                        // Skip the first 28 bytes (7 * 4 bytes - 1 for texture ID)
                        _baseStream.Position += 28;
                        var textureId = _reader.ReadUInt32();
                        
                        if (textureId > 0)
                        {
                            tiles.Add(new(col, row, textureId));
                        }
                    }
                }
                
                return tiles;
            }

             // Skip this chunk
            _baseStream.Position += header.Size;
        }

        // see if we've been incorrectly passed compressed BLTE data, I've been doing this too often
        _baseStream.Position = 0;
        var magic = _reader.ReadBytes(4);
        if (magic.Length == 4 && Encoding.ASCII.GetString(magic) == "BLTE")
            throw new InvalidDataException("WDT file appears to be BLTE data, decompress before passing");

        throw new InvalidDataException("WDT file did not contain a MAID chunk");
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

internal readonly record struct ChunkHeader(string Ident, uint Size);
