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

    // --- mip-chain slicing for cheap diffing ---------------------------------
    //
    // Unity's raw picture data for a texture with mipmaps is the base level
    // (mip 0, full resolution) followed by each successively smaller mip,
    // concatenated largest-to-smallest (this is the same layout Texture2D.
    // GetRawTextureData/LoadRawTextureData document for the managed side, and
    // it's what every raw Unity texture extractor - UABE, AssetStudio, etc. -
    // assumes). For a block-compressed format, each mip's byte size is
    // ceil(w/blockDim) * ceil(h/blockDim) * bytesPerBlock; for an uncompressed
    // format it's w*h*bytesPerPixel. Mip N's pixel dimensions are
    // max(1, baseDim >> N).
    //
    // This lets us decode a SMALL mip instead of the full-resolution base
    // level when all we want is the downsampled diff grid - the 48x48 grid
    // this project diffs against throws away all but ~0.1% of a full decode's
    // pixels anyway for a typical atlas, and the ASTC decoder in particular
    // (AstcSharp, pure C#, no SIMD) pays for every one of those discarded
    // pixels. Not supported for DXT5Crunched: crunch's own container format
    // doesn't lay out mips as simple concatenated raw blocks, so crunched
    // textures still decode at full resolution (this only matters for the
    // desktop/modded side, which is cheap - native Kyaru decode - regardless).
    private static bool IsSliceableFormat(int format) => format switch
    {
        FmtRGB24 or FmtRGBA32 or FmtDXT1 or FmtDXT5 or
        FmtETC2_RGB or FmtETC2_RGBA8 or
        FmtASTC_RGBA_4x4 or FmtASTC_RGBA_6x6 or FmtASTC_RGBA_8x8 => true,
        _ => false
    };

    private static (int blockDim, int bytesPerBlock) BlockLayout(int format) => format switch
    {
        FmtRGB24 => (1, 3),
        FmtRGBA32 => (1, 4),
        FmtDXT1 => (4, 8),
        FmtDXT5 => (4, 16),
        FmtETC2_RGB => (4, 8),
        FmtETC2_RGBA8 => (4, 16),
        FmtASTC_RGBA_4x4 => (4, 16),
        FmtASTC_RGBA_6x6 => (6, 16),
        FmtASTC_RGBA_8x8 => (8, 16),
        _ => throw new NotSupportedException($"no mip block layout for format {format}")
    };

    private static int MipByteSize(int format, int width, int height)
    {
        var (blockDim, bytesPerBlock) = BlockLayout(format);
        if (blockDim == 1)
            return checked(width * height * bytesPerBlock);

        int blocksX = (width + blockDim - 1) / blockDim;
        int blocksY = (height + blockDim - 1) / blockDim;
        return checked(blocksX * blocksY * bytesPerBlock);
    }

    private static (int width, int height) MipDimensions(int baseWidth, int baseHeight, int level) =>
        (Math.Max(1, baseWidth >> level), Math.Max(1, baseHeight >> level));

    /// <summary>
    /// Picks the smallest mip level (deepest into the chain, i.e. cheapest to
    /// decode) whose dimensions are still >= gridSize in both axes - anything
    /// smaller than the diff grid itself would just be upsampled noise for no
    /// benefit. Falls back to level 0 (the base image) if the texture has no
    /// mips, or if even the first mip drops below gridSize (small icon-sized
    /// textures - decoding the base level for those was already cheap).
    /// </summary>
    public static int ChooseDiffMipLevel(int baseWidth, int baseHeight, int mipCount, int gridSize)
    {
        int chosen = 0;
        for (int level = 1; level < mipCount; level++)
        {
            (int w, int h) = MipDimensions(baseWidth, baseHeight, level);
            if (w < gridSize || h < gridSize)
                break;
            chosen = level;
        }
        return chosen;
    }

    /// <summary>
    /// Attempts to slice a single mip level's encoded bytes out of a full
    /// mip-chain picture-data blob without decoding anything. Returns false
    /// (rather than throwing) for formats whose mip chain isn't a simple
    /// concatenation of raw blocks (currently just DXT5Crunched) or if the
    /// blob turns out shorter than the requested level's math implies - the
    /// caller should fall back to decoding the full base level in that case.
    /// </summary>
    public static bool TryGetMipSlice(
        byte[] pictureData, int format, int baseWidth, int baseHeight, int mipCount, int mipLevel,
        out byte[] mipBytes, out int mipWidth, out int mipHeight)
    {
        mipBytes = Array.Empty<byte>();
        mipWidth = mipHeight = 0;

        if (!IsSliceableFormat(format) || mipLevel < 0 || mipLevel >= Math.Max(1, mipCount))
            return false;

        int offset = 0;
        for (int level = 0; level < mipLevel; level++)
        {
            (int w, int h) = MipDimensions(baseWidth, baseHeight, level);
            offset += MipByteSize(format, w, h);
        }

        (int mw, int mh) = MipDimensions(baseWidth, baseHeight, mipLevel);
        int size = MipByteSize(format, mw, mh);

        if (offset < 0 || size < 0 || offset + size > pictureData.Length)
            return false; // mip chain shorter than expected - fall back to full decode

        var slice = new byte[size];
        Buffer.BlockCopy(pictureData, offset, slice, 0, size);

        mipBytes = slice;
        mipWidth = mw;
        mipHeight = mh;
        return true;
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

    // --- dimension-agnostic content comparison -------------------------------

    /// <summary>
    /// Box-downsamples an RGBA32 buffer of any width/height to a fixed
    /// gridSize x gridSize grid of averaged RGBA samples. This is what makes
    /// the diff dimension-agnostic: two textures that differ only because one
    /// side re-exported at a different resolution (or through a different
    /// lossy compressor) collapse to the same small grid and compare as
    /// "unchanged"; two textures with genuinely different painted content
    /// don't, regardless of their raw width/height.
    /// </summary>
    public static byte[] DownsampleToGrid(byte[] rgba32, int width, int height, int gridSize)
    {
        var grid = new byte[gridSize * gridSize * 4];

        for (int gy = 0; gy < gridSize; gy++)
        {
            int y0 = (int)((long)gy * height / gridSize);
            int y1 = (int)((long)(gy + 1) * height / gridSize);
            if (y1 <= y0) y1 = y0 + 1;
            y1 = Math.Min(y1, height);

            for (int gx = 0; gx < gridSize; gx++)
            {
                int x0 = (int)((long)gx * width / gridSize);
                int x1 = (int)((long)(gx + 1) * width / gridSize);
                if (x1 <= x0) x1 = x0 + 1;
                x1 = Math.Min(x1, width);

                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * width * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowBase + x * 4;
                        sumR += rgba32[i + 0];
                        sumG += rgba32[i + 1];
                        sumB += rgba32[i + 2];
                        sumA += rgba32[i + 3];
                        count++;
                    }
                }

                int gi = (gy * gridSize + gx) * 4;
                grid[gi + 0] = (byte)(sumR / count);
                grid[gi + 1] = (byte)(sumG / count);
                grid[gi + 2] = (byte)(sumB / count);
                grid[gi + 3] = (byte)(sumA / count);
            }
        }

        return grid;
    }

    /// <summary>
    /// 95th-percentile per-cell difference between two same-size grids
    /// (0-255 scale). Replaces a flat whole-grid mean, which two things were
    /// polluting: (1) cross-codec quantization noise (DXT5Crunched vs ASTC
    /// decode to slightly different RGB even for pixel-identical source art),
    /// spread evenly across every cell, and (2) alpha carrying full weight
    /// alongside RGB, which inflated the score for alpha-heavy sprite/UI
    /// textures whose alpha decode differs between decoders but whose visible
    /// content didn't change.
    ///
    /// Fix, two parts:
    ///   - Score per CELL, not per raw byte. Each cell collapses its 4
    ///     channels into one number, with alpha downweighted (kAlphaWeight)
    ///     relative to R/G/B, so decoder-level alpha noise can't drive the
    ///     score on its own the way full-weight alpha could.
    ///   - Take the 95th percentile across cells, not the mean. A real edit
    ///     (recolored costume, swapped region) lights up a cluster of cells
    ///     hard, and that cluster survives into the top 5%; uniform
    ///     low-magnitude codec noise sits in the bottom 95% and gets ignored
    ///     regardless of how many cells it touches.
    ///
    /// Log the score for every texture (see TransplantMode) so the threshold
    /// can be calibrated against real assets rather than guessed once and
    /// trusted forever - note the threshold's scale/behavior under this
    /// scorer differs from the old flat-mean one, so re-run --dry-run and
    /// re-check the logged scores rather than reusing an old threshold value
    /// unexamined.
    /// </summary>
    private const double kAlphaWeight = 0.25;

    public static double Percentile95CellDifference(byte[] gridA, byte[] gridB, int gridSize)
    {
        double[] cellScores = ComputeCellScores(gridA, gridB, gridSize);
        Array.Sort(cellScores);

        // Nearest-rank method: the 95th-percentile element of a sorted
        // ascending array of length N is at index ceil(0.95*N) - 1.
        int rank = (int)Math.Ceiling(0.95 * cellScores.Length) - 1;
        rank = Math.Clamp(rank, 0, cellScores.Length - 1);
        return cellScores[rank];
    }

    /// <summary>
    /// Fraction (0.0-1.0) of grid cells whose per-cell difference exceeds
    /// cellMagnitudeThreshold. This exists because Percentile95CellDifference
    /// alone can't distinguish "a modder recolored a real chunk of this
    /// texture" from "cross-codec quantization bias clustered along this
    /// texture's edges/text/high-contrast borders" - both produce a cluster
    /// of elevated cells, since block compressors lose the most precision
    /// exactly at hard edges, and almost every real texture has some. A
    /// genuine edit covers a real amount of surface area; edge-quantization
    /// noise, even when it clusters, stays a thin sliver along outlines.
    /// Pair this with a minimum-fraction check (see TransplantMode) instead
    /// of trusting a single worst cell to mean "changed".
    /// </summary>
    public static double ChangedAreaFraction(byte[] gridA, byte[] gridB, int gridSize, double cellMagnitudeThreshold)
    {
        double[] cellScores = ComputeCellScores(gridA, gridB, gridSize);
        int hot = 0;
        foreach (double score in cellScores)
        {
            if (score > cellMagnitudeThreshold) hot++;
        }
        return (double)hot / cellScores.Length;
    }

    private static double[] ComputeCellScores(byte[] gridA, byte[] gridB, int gridSize)
    {
        if (gridA.Length != gridB.Length)
            throw new ArgumentException("grids must be the same size to compare");

        int cellCount = gridSize * gridSize;
        int expected = checked(cellCount * 4);
        if (gridA.Length != expected)
            throw new ArgumentException(
                $"grid length {gridA.Length} doesn't match gridSize {gridSize} (expected {expected})");

        var cellScores = new double[cellCount];
        for (int c = 0; c < cellCount; c++)
        {
            int i = c * 4;
            double dr = Math.Abs(gridA[i + 0] - gridB[i + 0]);
            double dg = Math.Abs(gridA[i + 1] - gridB[i + 1]);
            double db = Math.Abs(gridA[i + 2] - gridB[i + 2]);
            double da = Math.Abs(gridA[i + 3] - gridB[i + 3]);
            cellScores[c] = (dr + dg + db + da * kAlphaWeight) / (3.0 + kAlphaWeight);
        }
        return cellScores;
    }

    /// <summary>
    /// Bilinear-resamples an RGBA32 buffer to a new width/height. Used only
    /// when a texture has been identified as genuinely changed and is about
    /// to be re-encoded: the modded (desktop) pixels are resampled to the
    /// ORIGINAL texture's own dimensions before encoding, so the transplanted
    /// texture keeps the original's memory/mip footprint instead of adopting
    /// whatever resolution the desktop asset happened to be authored at.
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
