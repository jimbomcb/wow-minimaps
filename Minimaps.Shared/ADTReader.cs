using System.Text;

namespace Minimaps.Shared;

/// <summary>
/// Reader for WoW ADT files containing terrain mesh data
/// https://wowdev.wiki/ADT/v18
/// </summary>
public class ADTReader : IDisposable
{
    public readonly record struct ChunkHeader(string Ident, uint Size);

    public readonly record struct MCNKHeader(
        uint IndexX,
        uint IndexY,
        float PositionX,
        float PositionY,
        float PositionZ
    );

    private readonly Stream _baseStream;
    private readonly BinaryReader _reader;
    private bool _disposed;

    public ADTReader(Stream baseStream)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _reader = new BinaryReader(_baseStream);
    }

    public List<MCNKChunkInfo> ReadMCNKChunks()
    {
        var chunks = new List<MCNKChunkInfo>();
        _baseStream.Position = 0;

        while (_baseStream.Position < _baseStream.Length)
        {
            var startPos = _baseStream.Position;
            var header = ReadChunkHeader();

            if (header.Ident == "MCNK")
            {
                var mcnkInfo = ParseMCNKChunk(startPos, header.Size);
                chunks.Add(mcnkInfo);
            }

            // next chunk
            _baseStream.Position = startPos + 8 + header.Size;
        }

        return chunks;
    }

    private MCNKChunkInfo ParseMCNKChunk(long chunkStartPos, uint chunkSize)
    {
        _baseStream.Position = chunkStartPos + 8; // Skip "MCNK" and size
        var headerStartPos = _baseStream.Position;

        // https://wowdev.wiki/ADT/v18#MCNK_chunk
        _reader.ReadUInt32(); // 0x000: flags
        var indexX = _reader.ReadUInt32(); // 0x004: IndexX
        var indexY = _reader.ReadUInt32(); // 0x008: IndexY

        // 0x068 (Position)
        _baseStream.Position = headerStartPos + 0x068;
        var positionX = _reader.ReadSingle(); // not sure why it's here when it always seems 0, and just implicit from index anyway
        var positionY = _reader.ReadSingle();
        var positionZ = _reader.ReadSingle();

        // end of header
        _baseStream.Position = headerStartPos + 128;

        var mcnkHeader = new MCNKHeader(
            IndexX: indexX,
            IndexY: indexY,
            PositionX: positionX,
            PositionY: positionY,
            PositionZ: positionZ
        );

        float[]? heights = ScanForMCVT(chunkStartPos, chunkSize, headerStartPos);
        return new MCNKChunkInfo(mcnkHeader, heights);
    }

    private float[]? ScanForMCVT(long mcnkStartPos, uint chunkSize, long headerStartPos)
    {
        // Scan the chunk data after the 128-byte header
        _baseStream.Position = headerStartPos + 128;
        var scanEndPos = mcnkStartPos + 8 + chunkSize;

        while (_baseStream.Position < scanEndPos - 8)
        {
            var magicBytes = _reader.ReadBytes(4);
            Array.Reverse(magicBytes);
            var magic = Encoding.ASCII.GetString(magicBytes);
            var size = _reader.ReadUInt32();

            if (magic == "MCVT")
            {
                const int EXPECTED_HEIGHT_COUNT = 145;
                if (size < EXPECTED_HEIGHT_COUNT * sizeof(float))
                    throw new Exception("Smaller than expected body in MCVT chunk");

                var heights = new float[EXPECTED_HEIGHT_COUNT];
                for (int i = 0; i < EXPECTED_HEIGHT_COUNT; i++)
                {
                    heights[i] = _reader.ReadSingle();
                }
                return heights;
            }
            else
            {
                if (_baseStream.Position + size <= scanEndPos)
                {
                    // Skip sub-chunk
                    _baseStream.Position += size;
                }
                else
                {
                    break;
                }
            }
        }

        return null;
    }

    private ChunkHeader ReadChunkHeader()
    {
        var identBytes = _reader.ReadBytes(4);
        Array.Reverse(identBytes);
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
        GC.SuppressFinalize(this);
    }

    public readonly record struct MCNKChunkInfo(MCNKHeader Header, float[]? Heights);
}
