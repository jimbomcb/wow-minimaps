namespace Minimaps.Tests;

public class AvifEncoderTests
{
    [Fact]
    public void Encode_Minimal_DoesNotCrash()
    {
        // AV1 needs at least 64x64
        var width = 64;
        var height = 64;
        var pixels = new byte[width * height * 4];
        Array.Fill<byte>(pixels, 255);
        using var ms = new MemoryStream();
        Minimaps.Services.AvifEncoder.Encode(pixels, width, height, 85, 4, ms);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Encode_SmallSolidImage_ProducesValidAvif()
    {
        // 64x64 solid red RGBA
        var width = 64;
        var height = 64;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;     // R
            pixels[i + 1] = 0;   // G
            pixels[i + 2] = 0;   // B
            pixels[i + 3] = 255; // A
        }

        using var ms = new MemoryStream();
        Minimaps.Services.AvifEncoder.Encode(pixels, width, height, 85, 4, ms);

        Assert.True(ms.Length > 0, "Output should not be empty");
        // AVIF files start with ftyp box
        ms.Position = 4;
        var ftyp = new byte[4];
        ms.Read(ftyp, 0, 4);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(ftyp));
    }

    [Fact]
    public void Encode_512x512_ProducesValidAvif()
    {
        var width = 512;
        var height = 512;
        var pixels = new byte[width * height * 4];
        var rng = new Random(42);
        rng.NextBytes(pixels);
        // Set alpha to 255
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        using var ms = new MemoryStream();
        Minimaps.Services.AvifEncoder.Encode(pixels, width, height, 85, 4, ms);

        Assert.True(ms.Length > 100, "512x512 AVIF should be more than 100 bytes");
    }

    [Fact]
    public void Encode_MultipleSequential_NoLeak()
    {
        var width = 128;
        var height = 128;
        var pixels = new byte[width * height * 4];
        new Random(42).NextBytes(pixels);

        // Encode 20 times, check memory doesn't explode
        var before = GC.GetTotalMemory(true);

        for (int i = 0; i < 20; i++)
        {
            using var ms = new MemoryStream();
            Minimaps.Services.AvifEncoder.Encode(pixels, width, height, 85, 4, ms);
            Assert.True(ms.Length > 0);
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var after = GC.GetTotalMemory(true);

        // Allow some growth but not crazy (should be well under 50MB for 20 small encodes)
        var growth = after - before;
        Assert.True(growth < 50 * 1024 * 1024, $"Memory grew by {growth / 1024 / 1024}MB over 20 encodes");
    }

    [Fact]
    public void Encode_Parallel_NoLeak()
    {
        var width = 128;
        var height = 128;
        var pixels = new byte[width * height * 4];
        new Random(42).NextBytes(pixels);

        var procBefore = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;

        Parallel.For(0, 50, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            using var ms = new MemoryStream();
            Minimaps.Services.AvifEncoder.Encode(pixels, width, height, 85, 4, ms);
            Assert.True(ms.Length > 0);
        });

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        var procAfter = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        var growth = procAfter - procBefore;

        // 50 parallel encodes should not grow by more than 500MB of private bytes
        // I have wasted too many hours fucking with NetVips and libheif memory issues to deal with this again
        Assert.True(growth < 500 * 1024 * 1024, $"Private bytes grew by {growth / 1024 / 1024}MB over 50 parallel encodes");
    }
}
