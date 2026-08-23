// BundleDoctor/TextureCodec.cs
//
// Shared Texture2D format constants + decode/encode helpers, factored out so
// both Program.cs (desktop -> iOS re-encode) and TransplantMode.cs (diff +
// selective transplant) work off one definition of "what format N means" and
// one decode/encode implementation per format, instead of two copies that can
// drift.
//
// Decode coverage is wider here than Program.cs ever needed on its own:
// Program.cs's convert pipeline only ever decoded DESKTOP-authored formats
// (RGB24/RGBA32/DXT1/DXT5/DXT5Crunched) because it re-encodes those into
// whatever the target format is. TransplantMode.cs additionally has to decode
// the ORIGINAL (already-iOS) bundle's own textures - ETC2/ASTC - purely to
// diff their pixels against the modded version, so decoders for those are
// added here.
//
// NuGet packages this file draws on (see BundleDoctor.csproj):
//   Kyaru.Texture2DDecoder  - RGB24/RGBA32 trivial unpack, DXT1/DXT5/DXT5Crunched,
//                             ETC2_RGB/ETC2_RGBA8 decode
//   AstcSharp               - ASTC 4x4/6x6 encode AND decode
//
using System;
using System.IO;
using System.Threading;
using AstcSharp;
using AstcSharp.Core;
using Texture2DDecoder;

internal static class TextureCodec
{
    // Unity TextureFormat values this pipeline knows about. Kept identical in
    // spelling/value to Program.cs's private copies - do not renumber.
    public const int FmtRGB24 = 3;
    public const int FmtRGBA32 = 4;
    public const int FmtDXT1 = 10;
    public const int FmtDXT5 = 12;
    public const int FmtDXT5Crunched = 29;
    public const int FmtETC2_RGB = 45;
    public const int FmtETC2_RGBA8 = 47;
    public const int FmtASTC_RGBA_4x4 = 48;
    public const int FmtASTC_RGBA_6x6 = 50;
    public const int FmtASTC_RGBA_8x8 = 51; // Unity ASTC 8x8

    public static string FormatName(int format) => format switch
    {
        FmtRGB24 => "RGB24",
        FmtRGBA32 => "RGBA32",
        FmtDXT1 => "DXT1",
        FmtDXT5 => "DXT5",
        FmtDXT5Crunched => "DXT5Crunched",
        FmtETC2_RGB => "ETC2_RGB",
        FmtETC2_RGBA8 => "ETC2_RGBA8",
        FmtASTC_RGBA_4x4 => "ASTC_RGBA_4x4",
        FmtASTC_RGBA_6x6 => "ASTC_RGBA_6x6",
        FmtASTC_RGBA_8x8 => "ASTC_RGBA_8x8",
        _ => $"Format{format}"
    };

    /// <summary>
    /// Decodes any Texture2D format this project knows how to read into a flat
    /// width*height*4 RGBA32 buffer, regardless of which side (desktop-authored
    /// modded bundle, or already-iOS original bundle) it came from. Throws
    /// NotSupportedException for anything not listed here - fails closed rather
    /// than silently treating an unknown format as "identical" during a diff.
    /// </summary>
    public static byte[] DecodeToRgba32(byte[] encodedData, int width, int height, int format, string texName)
    {
        switch (format)
        {
            case FmtRGB24:
                return DecodeRGB24(encodedData, width, height);

            case FmtRGBA32:
            {
                int expected = checked(width * height * 4);
                if (encodedData.Length < expected)
                    throw new InvalidDataException(
                        $"RGBA32 data too small for '{texName}': got {encodedData.Length}, expected at least {expected}");
                var rgba = new byte[expected];
                Buffer.BlockCopy(encodedData, 0, rgba, 0, expected);
                return rgba;
            }

            case FmtDXT1:
                return DecodeKyaruDXT(encodedData, width, height, isDxt5: false);

            case FmtDXT5:
                return DecodeKyaruDXT(encodedData, width, height, isDxt5: true);

            case FmtDXT5Crunched:
                return DecodeKyaruDXT5CrunchedGuarded(encodedData, width, height, texName);

            case FmtETC2_RGB:
                return DecodeKyaruETC2(encodedData, width, height, hasAlpha: false);

            case FmtETC2_RGBA8:
                return DecodeKyaruETC2(encodedData, width, height, hasAlpha: true);

            case FmtASTC_RGBA_4x4:
                return DecodeAstc(encodedData, width, height, FootprintType.Footprint4x4, texName);

            case FmtASTC_RGBA_6x6:
                return DecodeAstc(encodedData, width, height, FootprintType.Footprint6x6, texName);

            case FmtASTC_RGBA_8x8:
                return DecodeAstc(encodedData, width, height, FootprintType.Footprint8x8, texName);

            default:
                throw new NotSupportedException(
                    $"TextureCodec has no decoder for format {format} ('{texName}'); " +
                    "failing closed rather than assuming the pixels are unchanged.");
        }
    }

    /// <summary>
    /// Encodes RGBA32 pixels into the requested output format. Only the
    /// formats supported by the re-encoder are implemented here: RGBA32,
    /// ASTC 4x4/6x6/8x8, and ETC2 RGB/RGBA8. ETC2 is delegated to the vendored
    /// native wolfpld/etcpak encoder; no format silently falls back to another.
    /// </summary>
    public static byte[] EncodeFromRgba32(byte[] rgba32, int width, int height, int outputFormat, string texName)
    {
        switch (outputFormat)
        {
            case FmtRGBA32:
                return rgba32;

            case FmtASTC_RGBA_4x4:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint4x4, texName);

            case FmtASTC_RGBA_6x6:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint6x6, texName);

            case FmtASTC_RGBA_8x8:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint8x8, texName);

            case FmtETC2_RGB:
            case FmtETC2_RGBA8:
                return Etc2Encoder.Encode(rgba32, width, height, outputFormat, texName);

            default:
                throw new NotSupportedException(
                    $"TextureCodec has no encoder for output format {outputFormat} ('{texName}').");
        }
    }

    // --- decode backends ----------------------------------------------------

    private static byte[] DecodeRGB24(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 3);
        if (data.Length < expected)
            throw new InvalidDataException(
                $"RGB24 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 3, dst += 4)
        {
            rgba[dst + 0] = data[src + 0];
            rgba[dst + 1] = data[src + 1];
            rgba[dst + 2] = data[src + 2];
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeKyaruDXT(byte[] encodedData, int width, int height, bool isDxt5)
    {
        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];

        bool ok = isDxt5
            ? TextureDecoder.DecodeDXT5(encodedData, width, height, bgra)
            : TextureDecoder.DecodeDXT1(encodedData, width, height, bgra);

        if (!ok)
            throw new InvalidDataException($"Kyaru Texture2DDecoder failed to decode {(isDxt5 ? "DXT5" : "DXT1")}");

        return BgraToRgba(bgra);
    }

    // --- crunch decode isolation ---------------------------------------------
    //
    // Kyaru's UnpackUnityCrunch (the native Texture2DDecoder crunch/CRN
    // decompressor this calls into) has a public history of fatal native
    // crashes on certain crunch bitstreams (see e.g. AssetRipper#81), and can
    // apparently also just never return on some inputs rather than crash or
    // throw - a silent hang inside unmanaged code that .NET has no clean way
    // to cancel once it's started. Inside Parallel.ForEach, a single stuck
    // call like that permanently occupies one worker slot forever - on a
    // 2-vCPU CI runner that's HALF your parallelism gone silently, with
    // nothing logged for that texture because the call that would produce
    // the log line never returns. That reads from the outside as "the whole
    // job is unbearably slow", not as an obvious failure.
    //
    // Two defenses, both belt-and-braces given unmanaged code can't be
    // force-cancelled from managed .NET:
    //   - Serialize every crunch decode behind CrunchDecodeGate. If the hang
    //     is actually a reentrancy/thread-safety bug in the underlying CRN
    //     decoder (many crunch/CRN implementations lazily precompute shared
    //     Huffman tables with no synchronization - a classic source of
    //     exactly this kind of bug under concurrent first-use), never
    //     calling it from two threads at once fixes it outright.
    //   - Run the actual call on its own dedicated background thread and
    //     only wait up to CrunchDecodeTimeoutMs. If it doesn't return in
    //     time, give up on it (it's IsBackground so it can't block process
    //     exit, and the thread just leaks quietly for the rest of the run)
    //     and throw, so THIS texture is skipped via the normal item.Error
    //     path instead of the whole batch hanging until CI's job timeout
    //     kills it wholesale. The gate is deliberately never released once a
    //     call times out, so every later crunched texture in the run fails
    //     Monitor.TryEnter immediately instead of each queuing up to wait the
    //     full timeout again behind an abandoned call that will never finish.
    private static readonly object CrunchDecodeGate = new object();
    private const int CrunchDecodeTimeoutMs = 20_000;

    private static byte[] DecodeKyaruDXT5CrunchedGuarded(byte[] encodedData, int width, int height, string texName)
    {
        if (!Monitor.TryEnter(CrunchDecodeGate, CrunchDecodeTimeoutMs))
        {
            throw new TimeoutException(
                $"'{texName}': DXT5Crunched decode gate was still held after {CrunchDecodeTimeoutMs}ms - " +
                "an earlier crunched texture in this run appears to have hung inside the native decoder " +
                "rather than crashed or returned; skipping this texture rather than waiting on it indefinitely.");
        }

        byte[]? result = null;
        Exception? workerException = null;

        var worker = new Thread(() =>
        {
            try { result = DecodeKyaruDXT5Crunched(encodedData, width, height); }
            catch (Exception ex) { workerException = ex; }
        })
        {
            IsBackground = true,
            Name = "CrunchDecodeWorker"
        };
        worker.Start();

        if (!worker.Join(CrunchDecodeTimeoutMs))
        {
            // Deliberately NOT Monitor.Exit here - see class comment above.
            // The abandoned worker thread may still be sitting in the native
            // call; we're not waiting on it any further, and every later
            // crunched texture should fail fast rather than queue up behind it.
            throw new TimeoutException(
                $"'{texName}': DXT5Crunched decode did not return within {CrunchDecodeTimeoutMs}ms - " +
                "treating it as hung rather than waiting indefinitely. Every remaining DXT5Crunched " +
                "texture in this run will now be skipped too (see this message) until the process restarts.");
        }

        Monitor.Exit(CrunchDecodeGate);

        if (workerException != null)
            throw workerException;

        return result ?? throw new InvalidDataException($"'{texName}': DXT5Crunched decode returned no data");
    }

    private static byte[] DecodeKyaruDXT5Crunched(byte[] encodedData, int width, int height)
    {
        byte[]? unpacked = TextureDecoder.UnpackUnityCrunch(encodedData);
        if (unpacked == null || unpacked.Length == 0)
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to unpack UnityCrunch DXT5 data");

        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];
        if (!TextureDecoder.DecodeDXT5(unpacked, width, height, bgra))
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to decode unpacked UnityCrunch DXT5 data");

        return BgraToRgba(bgra);
    }

    private static byte[] DecodeKyaruETC2(byte[] encodedData, int width, int height, bool hasAlpha)
    {
        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];

        bool ok = hasAlpha
            ? TextureDecoder.DecodeETC2A8(encodedData, width, height, bgra)
            : TextureDecoder.DecodeETC2(encodedData, width, height, bgra);

        if (!ok)
            throw new InvalidDataException(
                $"Kyaru Texture2DDecoder failed to decode {(hasAlpha ? "ETC2_RGBA8" : "ETC2_RGB")}");

        return BgraToRgba(bgra);
    }

    // AstcSharp's decode entry point. NOTE: this project pins AstcSharp 3.1.0
    // (see BundleDoctor.csproj) and this call is written against that
    // version's stream-based shape to match how EncodeAstc below already
    // calls AstcEncoder.CompressImage(source, destination, ...) elsewhere in
    // this codebase. If the installed AstcSharp version instead only exposes
    // a Span-returning `AstcDecoder.ASTCDecompressToRGBA(bytes, w, h, footprint)`
    // (the shape older AstcSharp releases used), swap this one call - nothing
    // else in this file needs to change.
    private static byte[] DecodeAstc(byte[] encodedData, int width, int height, FootprintType footprintType, string texName)
    {
        using var source = new MemoryStream(encodedData, writable: false);
        using var destination = new MemoryStream();

        var footprint = Footprint.FromFootprintType(footprintType);
        AstcDecoder.DecompressImage(source, destination, width, height, footprint);
        byte[] rgba32 = destination.ToArray();

        int expected = checked(width * height * 4);
        if (rgba32.Length != expected)
        {
            throw new InvalidDataException(
                $"ASTC decode size mismatch for '{texName}': got {rgba32.Length:N0}, expected {expected:N0}");
        }

        return rgba32;
    }

    private static byte[] EncodeAstc(byte[] rgba32, int width, int height, FootprintType footprintType, string texName)
    {
        using var source = new MemoryStream(rgba32, writable: false);
        using var destination = new MemoryStream();

        var footprint = Footprint.FromFootprintType(footprintType);
        AstcEncoder.CompressImage(source, destination, width, height, footprint);
        byte[] blocks = destination.ToArray();

        int blockWidth = footprintType switch
        {
            FootprintType.Footprint4x4 => 4,
            FootprintType.Footprint6x6 => 6,
            FootprintType.Footprint8x8 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(footprintType), $"Unsupported ASTC footprint {footprintType}")
        };
        int expectedSize = checked(
            ((width + blockWidth - 1) / blockWidth) *
            ((height + blockWidth - 1) / blockWidth) *
            16);

        if (blocks.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"ASTC {blockWidth}x{blockWidth} size mismatch for '{texName}': " +
                $"got {blocks.Length:N0}, expected {expectedSize:N0}");
        }

        return blocks;
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i + 0];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }

    /// <summary>
    /// Bilinear-resamples an RGBA32 buffer to a new width/height. Used for
    /// every Texture2D the transplant identifies as mod-inlined (see
    /// TransplantMode's inline-size heuristic) and about to be re-encoded:
    /// the modded (desktop) pixels are resampled to the ORIGINAL texture's
    /// own dimensions before encoding, so the transplanted texture keeps the
    /// original's memory/mip footprint instead of adopting whatever
    /// resolution the desktop asset happened to be authored at.
    /// </summary>
    public static byte[] ResampleBilinear(byte[] srcRgba32, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth == dstWidth && srcHeight == dstHeight)
            return srcRgba32;

        var dst = new byte[dstWidth * dstHeight * 4];

        for (int dy = 0; dy < dstHeight; dy++)
        {
            double sy = (dy + 0.5) * srcHeight / dstHeight - 0.5;
            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;
            int y0c = Math.Clamp(y0, 0, srcHeight - 1);
            int y1c = Math.Clamp(y0 + 1, 0, srcHeight - 1);

            for (int dx = 0; dx < dstWidth; dx++)
            {
                double sx = (dx + 0.5) * srcWidth / dstWidth - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;
                int x0c = Math.Clamp(x0, 0, srcWidth - 1);
                int x1c = Math.Clamp(x0 + 1, 0, srcWidth - 1);

                int i00 = (y0c * srcWidth + x0c) * 4;
                int i10 = (y0c * srcWidth + x1c) * 4;
                int i01 = (y1c * srcWidth + x0c) * 4;
                int i11 = (y1c * srcWidth + x1c) * 4;

                int di = (dy * dstWidth + dx) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = srcRgba32[i00 + c] * (1 - fx) + srcRgba32[i10 + c] * fx;
                    double bot = srcRgba32[i01 + c] * (1 - fx) + srcRgba32[i11 + c] * fx;
                    dst[di + c] = (byte)Math.Round(top * (1 - fy) + bot * fy);
                }
            }
        }

        return dst;
    }
}
