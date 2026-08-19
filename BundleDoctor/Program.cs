// BundleDoctor/Program.cs
//
// Doctors a Limbus Company DESKTOP asset bundle into an iOS-loadable one.
//   - retargets SerializedFile TargetPlatform -> iOS (9)
//   - converts only these source Texture2D formats:
//       RGB24 (3), RGBA32 (4), DXT1 (10), DXT5 (12), DXT5Crunched (29)
//   - leaves ASTC RGBA 4x4 (48) and ASTC RGBA 6x6 (50) untouched
//   - decodes DXT/DXT5Crunched using Kyaru.Texture2DDecoder (the Unity Texture2D
//     decoder used by AssetStudio), with explicit UnityCrunch unpacking for format 29
//   - re-encodes every converted texture to a configurable output format
//     (default: RGBA32); ASTC 4x4/6x6 use AstcSharp and ETC2 uses the optional native encoder
//   - moves converted texture data inline and clears m_StreamData so the rewritten
//     Texture2D does not depend on hand-written .resS offsets
//   - materializes AssetsTools.NET replacers with Write(), then reloads and repacks
//     the materialized bundle as LZ4HC
//
// NuGet:
//   AssetsTools.NET 3.0.2
//   AssetsTools.NET.Texture 3.0.2 (raw Texture2D data access only)
//   Kyaru.Texture2DDecoder 0.17.1 + Kyaru.Texture2DDecoder.Linux 0.2.0
//   AstcSharp 3.1.0
//
// Usage: BundleDoctor <input.bundle> <output.bundle> [outputFormat] [classdata.tpk]
//
using System;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using AstcSharp;
using AstcSharp.Core;
using Texture2DDecoder;

internal static class Program
{
    // Unity BuildTarget values (from Unity's own tooling / editor source)
    private const int kTargetWindows64 = 19;
    private const int kTargetIOS = 9;

    // TextureFormat values relevant to this pipeline
    private const int kFmtRGB24 = 3;
    private const int kFmtRGBA32 = 4;
    private const int kFmtDXT1 = 10;
    private const int kFmtDXT5 = 12;
    private const int kFmtDXT5Crunched = 29;
    private const int kFmtETC2_RGB = 45;
    private const int kFmtETC2_RGBA8 = 47;
    private const int kFmtASTC_RGBA_4x4 = 48;
    private const int kFmtASTC_RGBA_6x6 = 50;

    // Default output format. The workflow can override this with the final CLI argument.
    // Accepted names: RGBA32, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, ASTC_RGBA_6x6.
    private const int kDefaultOutputTextureFormat = kFmtRGBA32;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: BundleDoctor <input.bundle> <output.bundle> [outputFormat] [classdata.tpk]");
            return 2;
        }

        string inputPath = args[0];
        string outputPath = args[1];

        string? tpkPath = null;
        string outputFormatName = "RGBA32";

        if (args.Length >= 3)
        {
            // If the third argument is a recognized format, treat it as the output format.
            // Otherwise retain the legacy third-argument classdata.tpk position.
            if (IsOutputTextureFormatName(args[2]))
                outputFormatName = args[2];
            else
                tpkPath = args[2];
        }

        if (args.Length >= 4)
            outputFormatName = args[3];

        int outputTextureFormat =
            ParseOutputTextureFormat(outputFormatName, kDefaultOutputTextureFormat);

        Console.WriteLine(
            $"[config] OutputTextureFormat={FormatName(outputTextureFormat)} ({outputTextureFormat})");

        var manager = new AssetsManager();
        if (tpkPath != null)
        {
            manager.LoadClassPackage(tpkPath);
        }

        BundleFileInstance bunInst = manager.LoadBundleFile(inputPath, unpackIfPacked: true);
        AssetBundleFile bundle = bunInst.file;

        int convertedCount = 0;
        int totalTextures = 0;
        int touchedFiles = 0;
        int retargetedFiles = 0;

        for (int dirIndex = 0; dirIndex < bundle.BlockAndDirInfo.DirectoryInfos.Count; dirIndex++)
        {
            var dirInfo = bundle.BlockAndDirInfo.DirectoryInfos[dirIndex];
            if (!LooksLikeSerializedFile(dirInfo.Name))
                continue;

            AssetsFileInstance afileInst;
            try
            {
                afileInst = manager.LoadAssetsFileFromBundle(bunInst, dirIndex, loadDeps: false);
            }
            catch (Exception)
            {
                // Not every non-.resS entry is necessarily a serialized file (e.g. loose
                // .resource blobs with no recognizable suffix) -- skip anything that fails
                // to parse as one rather than guessing.
                continue;
            }

            AssetsFile af = afileInst.file;

            if (tpkPath != null)
            {
                manager.LoadClassDatabaseFromPackage(af.Metadata.UnityVersion);
            }

            bool fileTouched = false;

            // --- 1. Retarget platform ---------------------------------------------
            uint originalTargetPlatform = af.Metadata.TargetPlatform;
            Console.WriteLine(
                $"[{dirInfo.Name}] TargetPlatform before: {originalTargetPlatform} -> requested {kTargetIOS}");

            // Do this unconditionally rather than only when the source says 19.
            // The caller has already identified this as a SerializedFile we want to
            // doctor, so there is no benefit in leaving a non-iOS platform value in it.
            if (originalTargetPlatform != kTargetIOS)
            {
                af.Metadata.TargetPlatform = kTargetIOS;
                fileTouched = true;
                retargetedFiles++;
            }

            Console.WriteLine(
                $"[{dirInfo.Name}] TargetPlatform after:  {af.Metadata.TargetPlatform}");

            // --- 2. Walk Texture2D objects ------------------------------------------
            foreach (AssetFileInfo info in af.GetAssetsOfType(AssetClassID.Texture2D))
            {
                totalTextures++;
                AssetTypeValueField baseField = manager.GetBaseField(afileInst, info);

                int format = baseField["m_TextureFormat"].AsInt;
                if (!NeedsConversion(format))
                {
                    // Formats 48 (ASTC RGBA 4x4) and 50 (ASTC RGBA 6x6) are already
                    // supported by the target iOS build, so leave them byte-for-byte alone.
                    continue;
                }

                int width = baseField["m_Width"].AsInt;
                int height = baseField["m_Height"].AsInt;
                string texName = baseField["m_Name"].AsString;

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"invalid dimensions for '{texName}': {width}x{height}");

                // AssetsTools.NET.Texture is retained only for reliably resolving inline
                // image data vs. streamed .resS data. The actual compressed-texture decoder
                // is Kyaru.Texture2DDecoder, which is the same Unity Texture2D decoder used
                // by AssetStudio and has explicit UnityCrunch handling.
                TextureFile tf = TextureFile.ReadTextureFile(baseField);
                byte[] encodedData = tf.FillPictureData(afileInst)
                    ?? throw new InvalidDataException($"could not load texture data for '{texName}'");

                byte[] rgba32;
                switch (format)
                {
                    case kFmtRGB24:
                        rgba32 = DecodeRGB24(encodedData, width, height);
                        break;

                    case kFmtRGBA32:
                        int expectedRgbaBytes = checked(width * height * 4);
                        if (encodedData.Length < expectedRgbaBytes)
                        {
                            throw new InvalidDataException(
                                $"RGBA32 data too small for '{texName}': got {encodedData.Length}, " +
                                $"expected at least {expectedRgbaBytes}");
                        }

                        // Unity TextureFormat.RGBA32 is already RGBA byte order.
                        rgba32 = new byte[expectedRgbaBytes];
                        Buffer.BlockCopy(encodedData, 0, rgba32, 0, expectedRgbaBytes);
                        break;

                    case kFmtDXT1:
                        rgba32 = DecodeKyaruDXT(encodedData, width, height, isDxt5: false);
                        break;

                    case kFmtDXT5:
                        rgba32 = DecodeKyaruDXT(encodedData, width, height, isDxt5: true);
                        break;

                    case kFmtDXT5Crunched:
                        rgba32 = DecodeKyaruDXT5Crunched(encodedData, width, height);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"conversion dispatch missing for texture format {format} ('{texName}')");
                }

                int expectedDecodedSize = checked(width * height * 4);
                if (rgba32.Length != expectedDecodedSize)
                {
                    throw new InvalidDataException(
                        $"decoded RGBA size mismatch for '{texName}' (format {format}): " +
                        $"got {rgba32.Length}, expected {expectedDecodedSize}");
                }

                // Encode the decoded RGBA32 pixels according to the selected output format.
                byte[] outputData = EncodeOutputTexture(
                    rgba32,
                    width,
                    height,
                    outputTextureFormat,
                    texName);

                Console.WriteLine(
                    $"[Texture] '{texName}' {width}x{height}: format {format} -> " +
                    $"{FormatName(outputTextureFormat)} ({outputTextureFormat}), " +
                    $"{encodedData.Length:N0} -> {outputData.Length:N0} bytes");

                // --- 3. Write the selected format back into the Texture2D -------------
                baseField["m_TextureFormat"].AsInt = outputTextureFormat;
                baseField["m_MipCount"].AsInt = 1;
                baseField["m_CompleteImageSize"].AsInt = outputData.Length;

                AssetTypeValueField streamData = baseField["m_StreamData"];
                streamData["offset"].AsULong = 0;
                streamData["size"].AsInt = 0;
                streamData["path"].AsString = string.Empty;
                baseField["image data"].AsByteArray = outputData;

                info.SetNewData(baseField); // re-serializes just this object
                convertedCount++;
                fileTouched = true;
            }

            // --- 4. Write the modified SerializedFile back into the bundle directory ---
            if (fileTouched)
            {
                dirInfo.SetNewData(af);
                touchedFiles++;
            }
        }

        // IMPORTANT: AssetBundleFile.Pack() streams DataReader.BaseStream directly and
        // does not apply DirectoryInfo.Replacer objects. That means calling Pack()
        // immediately after SetNewData(af) silently discards BOTH the target-platform
        // change and any Texture2D replacements.
        //
        // Therefore the write pipeline must be two-stage:
        //   1. Write() -> materialize all replacers into a fresh, uncompressed bundle.
        //   2. Reload that fresh bundle -> Pack() it as LZ4HC.
        string tempUnpackedPath = Path.Combine(
            Path.GetTempPath(),
            $"BundleDoctor-{Guid.NewGuid():N}.unity3d");

        try
        {
            using (var tempStream = File.Create(tempUnpackedPath))
            using (var tempWriter = new AssetsFileWriter(tempStream))
            {
                bundle.Write(tempWriter, 0);
            }

            // Reload the materialized bundle so Pack() reads the doctored bytes rather
            // than the original bundle's DataReader stream.
            var repackManager = new AssetsManager();
            try
            {
                BundleFileInstance materialized = repackManager.LoadBundleFile(
                    tempUnpackedPath, unpackIfPacked: true);

                using (var outStream = File.Create(outputPath))
                using (var writer = new AssetsFileWriter(outStream))
                {
                    // AssetsTools.NET's LZ4 mode uses the Encode32HC path for the
                    // high-compression blocks.
                    materialized.file.Pack(
                        writer,
                        AssetBundleCompressionType.LZ4,
                        blockDirAtEnd: true);
                }

                materialized.file.Close();
            }
            finally
            {
                repackManager.UnloadAll();
            }
        }
        finally
        {
            try { File.Delete(tempUnpackedPath); } catch { }
        }

        // Final verification: reload the actual output and make sure the serialized
        // files that can be parsed are now targeting iOS. This turns a silent no-op
        // into a workflow failure instead of handing the tweak a Windows-targeted bundle.
        var verifyManager = new AssetsManager();
        try
        {
            BundleFileInstance verifyBundle = verifyManager.LoadBundleFile(outputPath, unpackIfPacked: true);
            int verifiedSerializedFiles = 0;
            int verifiedTextures = 0;
            int remainingDesktopTextureFormats = 0;

            for (int i = 0; i < verifyBundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                var verifyDir = verifyBundle.file.BlockAndDirInfo.DirectoryInfos[i];
                if (!LooksLikeSerializedFile(verifyDir.Name))
                    continue;

                try
                {
                    AssetsFileInstance verifyFile = verifyManager.LoadAssetsFileFromBundle(
                        verifyBundle, i, loadDeps: false);

                    uint target = verifyFile.file.Metadata.TargetPlatform;
                    Console.WriteLine(
                        $"[verify] {verifyDir.Name}: TargetPlatform={target}");

                    verifiedSerializedFiles++;
                    if (target != kTargetIOS)
                    {
                        Console.Error.WriteLine(
                            $"ERROR: {verifyDir.Name} still targets platform {target}; expected {kTargetIOS}.");
                        return 1;
                    }

                    foreach (AssetFileInfo verifyInfo in verifyFile.file.GetAssetsOfType(AssetClassID.Texture2D))
                    {
                        verifiedTextures++;
                        AssetTypeValueField verifyBase = verifyManager.GetBaseField(verifyFile, verifyInfo);
                        int verifyFormat = verifyBase["m_TextureFormat"].AsInt;
                        if (NeedsConversion(verifyFormat))
                            remainingDesktopTextureFormats++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"ERROR: could not verify serialized file '{verifyDir.Name}': {ex.Message}");
                    return 1;
                }
            }

            if (verifiedSerializedFiles == 0)
            {
                Console.Error.WriteLine("ERROR: output bundle contains no verifiable SerializedFiles.");
                return 1;
            }

            if (remainingDesktopTextureFormats != 0)
            {
                Console.Error.WriteLine(
                    $"ERROR: output still contains {remainingDesktopTextureFormats} texture(s) " +
                    "using a format that this doctor is supposed to convert.");
                return 1;
            }

            Console.WriteLine(
                $"[verify] SerializedFiles={verifiedSerializedFiles}, Texture2D={verifiedTextures}, " +
                "all converted-source formats removed.");
        }
        finally
        {
            verifyManager.UnloadAll();
        }

        Console.WriteLine(
            $"Converted {convertedCount}/{totalTextures} textures across {touchedFiles} " +
            $"serialized file(s); retargeted {retargetedFiles} file(s). Wrote and verified {outputPath}.");
        return 0;
    }

    private static bool NeedsConversion(int format) => format switch
    {
        // Desktop-only / unsupported formats: decode then re-encode.
        kFmtRGB24 => true,
        kFmtRGBA32 => true,
        kFmtDXT1 => true,
        kFmtDXT5 => true,
        kFmtDXT5Crunched => true,

        // Already-supported / already-converted formats: leave untouched.
        kFmtETC2_RGB => false,
        kFmtETC2_RGBA8 => false,
        kFmtASTC_RGBA_4x4 => false,
        kFmtASTC_RGBA_6x6 => false,

        _ => throw new NotSupportedException(
            $"texture format {format} has no conversion rule; failing closed rather than guessing")
    };

    private static bool IsOutputTextureFormatName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim().ToUpperInvariant() switch
        {
            "RGBA32" => true,
            "ETC2_RGB" => true,
            "ETC2_RGBA8" => true,
            "ASTC_RGBA_4X4" => true,
            "ASTC_RGBA_6X6" => true,
            "3" => true,
            "4" => true,
            "45" => true,
            "47" => true,
            "48" => true,
            "50" => true,
            _ => false
        };
    }

    private static int ParseOutputTextureFormat(string value, int defaultFormat)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultFormat;

        return value.Trim().ToUpperInvariant() switch
        {
            "RGBA32" => kFmtRGBA32,
            "ETC2_RGB" => kFmtETC2_RGB,
            "ETC2_RGBA8" => kFmtETC2_RGBA8,
            "ASTC_RGBA_4X4" => kFmtASTC_RGBA_4x4,
            "ASTC_RGBA_6X6" => kFmtASTC_RGBA_6x6,
            "3" => kFmtRGB24,
            "4" => kFmtRGBA32,
            "45" => kFmtETC2_RGB,
            "47" => kFmtETC2_RGBA8,
            "48" => kFmtASTC_RGBA_4x4,
            "50" => kFmtASTC_RGBA_6x6,
            _ => throw new ArgumentException(
                $"Unknown output texture format '{value}'. " +
                "Use RGBA32, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, or ASTC_RGBA_6x6.")
        };
    }

    private static string FormatName(int format) => format switch
    {
        kFmtRGBA32 => "RGBA32",
        kFmtETC2_RGB => "ETC2_RGB",
        kFmtETC2_RGBA8 => "ETC2_RGBA8",
        kFmtASTC_RGBA_4x4 => "ASTC_RGBA_4x4",
        kFmtASTC_RGBA_6x6 => "ASTC_RGBA_6x6",
        _ => $"Format{format}"
    };

    private static byte[] EncodeOutputTexture(
        byte[] rgba32,
        int width,
        int height,
        int outputFormat,
        string texName)
    {
        switch (outputFormat)
        {
            case kFmtRGBA32:
                // No compression: directly store Unity RGBA32 pixels.
                return rgba32;

            case kFmtASTC_RGBA_4x4:
                return EncodeAstc(
                    rgba32,
                    width,
                    height,
                    FootprintType.Footprint4x4,
                    outputFormat,
                    texName);

            case kFmtASTC_RGBA_6x6:
                return EncodeAstc(
                    rgba32,
                    width,
                    height,
                    FootprintType.Footprint6x6,
                    outputFormat,
                    texName);

            case kFmtETC2_RGB:
            case kFmtETC2_RGBA8:
                throw new NotSupportedException(
                    $"ETC2 output ({FormatName(outputFormat)}) is not available in this Kyaru-based " +
                    "build. Remove this case or add the native ETC2 encoder before selecting it.");

            default:
                throw new NotSupportedException(
                    $"Output texture format {outputFormat} is not implemented.");
        }
    }

    private static byte[] EncodeAstc(
        byte[] rgba32,
        int width,
        int height,
        FootprintType footprintType,
        int outputFormat,
        string texName)
    {
        using var source = new MemoryStream(rgba32, writable: false);
        using var destination = new MemoryStream();

        var footprint = Footprint.FromFootprintType(footprintType);
        AstcEncoder.CompressImage(source, destination, width, height, footprint);
        byte[] blocks = destination.ToArray();

        int blockWidth = footprintType == FootprintType.Footprint4x4 ? 4 : 6;
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
            rgba[dst + 0] = data[src + 0]; // R
            rgba[dst + 1] = data[src + 1]; // G
            rgba[dst + 2] = data[src + 2]; // B
            rgba[dst + 3] = 255;           // A
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
        {
            throw new InvalidDataException(
                $"Kyaru Texture2DDecoder failed to decode {(isDxt5 ? "DXT5" : "DXT1")}");
        }

        return BgraToRgba(bgra);
    }

    private static byte[] DecodeKyaruDXT5Crunched(byte[] encodedData, int width, int height)
    {
        // Limbus Company is a modern Unity build (Unity 6), so use UnityCrunch explicitly.
        // This avoids relying on the serialized Unity-version metadata, which may be obfuscated.
        byte[]? unpacked = TextureDecoder.UnpackUnityCrunch(encodedData);
        if (unpacked == null || unpacked.Length == 0)
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to unpack UnityCrunch DXT5 data");

        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];
        if (!TextureDecoder.DecodeDXT5(unpacked, width, height, bgra))
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to decode unpacked UnityCrunch DXT5 data");

        return BgraToRgba(bgra);
    }

    private static byte[] EncodeAstc6x6(byte[] rgba32, int width, int height)
    {
        using var source = new MemoryStream(rgba32, writable: false);
        using var destination = new MemoryStream();

        var footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        AstcEncoder.CompressImage(source, destination, width, height, footprint);
        return destination.ToArray();
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2]; // R <- B
            rgba[i + 1] = bgra[i + 1]; // G <- G
            rgba[i + 2] = bgra[i + 0]; // B <- R
            rgba[i + 3] = bgra[i + 3]; // A <- A
        }
        return rgba;
    }

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
}
