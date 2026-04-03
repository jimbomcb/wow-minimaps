using System.Runtime.InteropServices;

namespace Minimaps.Services;

/// <summary>
/// AVIF encoder using libavif (direct P/Invoke to the reference AVIF library)
/// I'm going through all this effort because there's god awful memory handling in every other AVIF library I can find.
/// Thread-safe: each call creates independent encoder/image instances.
/// </summary>
public static class AvifEncoder
{
    /// <summary>
    /// Encode RGBA pixel data to AVIF, write to the output stream.
    /// </summary>
    public static void Encode(byte[] rgbaPixels, int width, int height, int quality, int speed, Stream output)
    {
        var image = avifImageCreate((uint)width, (uint)height, 8, AVIF_PIXEL_FORMAT_YUV420);
        if (image == 0)
            throw new InvalidOperationException("avifImageCreate failed");

        try
        {
            AvifRGBImage rgb = default;
            avifRGBImageSetDefaults(ref rgb, image);

            var pinned = GCHandle.Alloc(rgbaPixels, GCHandleType.Pinned);
            try
            {
                rgb.pixels = pinned.AddrOfPinnedObject();
                rgb.rowBytes = (uint)(width * 4);

                var result = avifImageRGBToYUV(image, ref rgb);
                if (result != AVIF_RESULT_OK)
                    throw new InvalidOperationException($"avifImageRGBToYUV failed: {ResultToString(result)}");
            }
            finally
            {
                pinned.Free();
            }

            var encoder = avifEncoderCreate();
            if (encoder == 0)
                throw new InvalidOperationException("avifEncoderCreate failed");

            try
            {
                // encoder fields at known struct offsets
                // Layout: codecChoice(4), maxThreads(4), speed(4), keyframeInterval(4), timescale(8), repetitionCount(4), extraLayerCount(4), quality(4), qualityAlpha(4)
                Marshal.WriteInt32(encoder + 4, 1);        // maxThreads = 1 (thread safety via caller)
                Marshal.WriteInt32(encoder + 8, speed);    // speed
                Marshal.WriteInt32(encoder + 32, quality); // quality
                Marshal.WriteInt32(encoder + 36, quality); // qualityAlpha (same as quality)

                AvifRWData avifOutput = default;
                var encResult = avifEncoderWrite(encoder, image, ref avifOutput);
                if (encResult != AVIF_RESULT_OK)
                    throw new InvalidOperationException($"avifEncoderWrite failed: {ResultToString(encResult)}");

                try
                {
                    var buffer = new byte[(int)avifOutput.size];
                    Marshal.Copy(avifOutput.data, buffer, 0, buffer.Length);
                    output.Write(buffer);
                }
                finally
                {
                    avifRWDataFree(ref avifOutput);
                }
            }
            finally
            {
                avifEncoderDestroy(encoder);
            }
        }
        finally
        {
            avifImageDestroy(image);
        }
    }

    private static string ResultToString(int result)
    {
        var ptr = avifResultToString(result);
        return ptr != 0 ? Marshal.PtrToStringAnsi(ptr) ?? "unknown" : "unknown";
    }

    private const int AVIF_PIXEL_FORMAT_YUV420 = 3;
    private const int AVIF_RESULT_OK = 0;


    [StructLayout(LayoutKind.Sequential)]
    private struct AvifRGBImage
    {
        public uint width;
        public uint height;
        public uint depth;
        public int format;
        public int chromaUpsampling;
        public int chromaDownsampling;
        public int avoidLibYUV;
        public int ignoreAlpha;
        public int alphaPremultiplied;
        public int isFloat;
        public int maxThreads;
        public nint pixels;
        public uint rowBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AvifRWData
    {
        public nint data;
        public nuint size;
    }

    private const string Lib = "avif";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint avifImageCreate(uint width, uint height, uint depth, int yuvFormat);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void avifImageDestroy(nint image);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void avifRGBImageSetDefaults(ref AvifRGBImage rgb, nint image);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int avifImageRGBToYUV(nint image, ref AvifRGBImage rgb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint avifEncoderCreate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int avifEncoderWrite(nint encoder, nint image, ref AvifRWData output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void avifEncoderDestroy(nint encoder);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void avifRWDataFree(ref AvifRWData raw);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint avifResultToString(int result);
}
