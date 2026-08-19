// BundleDoctor/Program.cs
//
// POC: doctors a Limbus Company DESKTOP asset bundle into an iOS-loadable one.
//   - retargets SerializedFile m_TargetPlatform 19 (StandaloneWindows64) -> 9 (iOS)
//   - re-encodes any Texture2D whose format iOS can't sample (DXT1/DXT5/DXT5Crunched/RGB24)
//     into RGBA32, leaving already-iOS-safe formats (RGBA32, ASTC 4x4/6x6) untouched
//   - handles both inline image data and streamed (.resS) texture data
//
// Deps (NuGet):
//   AssetsTools.NET, AssetsTools.NET.Texture   (schema-driven field access, no manual offsets)
//   Texture2DDecoder (K0lb3/AssetStudio-style native decode bindings for DXT1/DXT5/BC7/etc.)
//
// Usage: BundleDoctor <input.bundle> <output.bundle>

using System;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Kyaru.Texture2DDecoder; // thin wrapper around Texture2DDecoder's DecodeDXT1/DecodeDXT5/UnpackCrunch

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
    private const int kFmtASTC_RGBA_4x4 = 48;
    private const int kFmtASTC_RGBA_6x6 = 50;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: BundleDoctor <input.bundle> <output.bundle>");
            return 2;
        }

        string inputPath = args[0];
        string outputPath = args[1];

        var manager = new AssetsManager();

        // Bundles are loaded as a container (UnityFS) holding one or more SerializedFiles.
        BundleFileInstance bundleInst = manager.LoadBundleFile(inputPath, unpackIfPacked: true);
        AssetBundleFile bundle = bundleInst.file;

        int convertedCount = 0;
        int totalTextures = 0;

        for (int dirIndex = 0; dirIndex < bundle.BlockAndDirInfo.DirectoryInfos.Count; dirIndex++)
        {
            var dirInfo = bundle.BlockAndDirInfo.DirectoryInfos[dirIndex];
            if (!dirInfo.Name.EndsWith(".resS") && LooksLikeSerializedFile(bundle, dirIndex))
            {
                AssetsFileInstance afi = manager.LoadAssetsFileFromBundle(bundleInst, dirIndex, loadDeps: false);
                AssetsFile af = afi.file;

                // --- 1. Retarget platform -----------------------------------------
                if (af.Metadata.TargetPlatform == kTargetWindows64)
                {
                    af.Metadata.TargetPlatform = kTargetIOS;
                }

                // --- 2. Walk Texture2D objects -------------------------------------
                var texInfos = af.GetAssetsOfType(AssetClassID.Texture2D);
                foreach (AssetFileInfo info in texInfos)
                {
                    totalTextures++;
                    AssetTypeValueField baseField = manager.GetBaseField(afi, info);

                    int format = baseField["m_TextureFormat"].AsInt;
                    if (!NeedsConversion(format))
                        continue; // already RGBA32/ASTC — leave completely untouched

                    int width = baseField["m_Width"].AsInt;
                    int height = baseField["m_Height"].AsInt;

                    AssetTypeValueField streamData = baseField["m_StreamData"];
                    string streamPath = streamData["path"].AsString;
                    bool isStreamed = !string.IsNullOrEmpty(streamPath);

                    byte[] sourceBytes;
                    if (isStreamed)
                    {
                        long offset = (long)streamData["offset"].AsULong;
                        int size = streamData["size"].AsInt;
                        sourceBytes = ReadFromResS(bundle, streamPath, offset, size);
                    }
                    else
                    {
                        sourceBytes = baseField["image data"].AsByteArray;
                    }

                    byte[] rgba32;
                    using (var pool = new AutoreleaseScope())
                    {
                        rgba32 = DecodeToRGBA32(sourceBytes, width, height, format);
                    }

                    if (rgba32.Length != width * height * 4)
                        throw new InvalidDataException(
                            $"decoded size mismatch for {baseField["m_Name"].AsString}: " +
                            $"got {rgba32.Length}, expected {width * height * 4}");

                    // --- 3. Patch fields back via the type tree (no offset math) ---
                    baseField["m_TextureFormat"].AsInt = kFmtRGBA32;
                    baseField["m_MipCount"].AsInt = 1;
                    baseField["m_CompleteImageSize"].AsInt = rgba32.Length;

                    if (isStreamed)
                    {
                        // Keep it streamed: append to the bundle's .resS node instead of
                        // inflating the serialized object (matches the "disk, not RAM" rule).
                        long newOffset = AppendToResS(bundle, streamPath, rgba32);
                        streamData["offset"].AsULong = (ulong)newOffset;
                        streamData["size"].AsInt = rgba32.Length;
                        baseField["image data"].AsByteArray = Array.Empty<byte>();
                    }
                    else
                    {
                        baseField["image data"].AsByteArray = rgba32;
                        streamData["offset"].AsULong = 0;
                        streamData["size"].AsInt = 0;
                        streamData["path"].AsString = string.Empty;
                    }

                    info.SetNewData(baseField); // re-serializes just this object
                    convertedCount++;
                }

                // Write the modified SerializedFile back into the bundle's directory entry
                using var afStream = new MemoryStream();
                af.Write(new AssetsFileWriter(afStream));
                ReplaceBundleEntry(bundle, dirIndex, afStream.ToArray());
            }
        }

        using (var outStream = File.Create(outputPath))
        {
            bundle.Write(new AssetsFileWriter(outStream));
        }

        Console.WriteLine($"Converted {convertedCount}/{totalTextures} textures. Wrote {outputPath}.");
        return 0;
    }

    private static bool NeedsConversion(int format) => format switch
    {
        kFmtRGB24 => true,
        kFmtDXT1 => true,
        kFmtDXT5 => true,
        kFmtDXT5Crunched => true,
        kFmtRGBA32 => false,
        kFmtASTC_RGBA_4x4 => false,
        kFmtASTC_RGBA_6x6 => false,
        _ => throw new NotSupportedException(
            $"texture format {format} has no conversion rule; failing closed rather than guessing")
    };

    private static byte[] DecodeToRGBA32(byte[] src, int w, int h, int format)
    {
        return format switch
        {
            kFmtRGB24 => TextureDecoder.DecodeRGB24(src, w, h),
            kFmtDXT1 => TextureDecoder.DecodeDXT1(src, w, h),
            kFmtDXT5 => TextureDecoder.DecodeDXT5(src, w, h),
            kFmtDXT5Crunched => TextureDecoder.DecodeDXT5(
                TextureDecoder.UnpackCrunch(src, w, h), w, h),
            _ => throw new NotSupportedException($"no decoder registered for format {format}")
        };
    }

    // --- bundle-level helpers -------------------------------------------------
    // These are the pieces you already have working equivalents of in
    // UnityBundleCAB.h/.m — shown here as the AssetsTools.NET-side counterparts
    // so the two ends of the pipeline agree on the .resS layout.

    private static bool LooksLikeSerializedFile(AssetBundleFile bundle, int dirIndex) =>
        !bundle.BlockAndDirInfo.DirectoryInfos[dirIndex].Name.Contains(".resource");

    private static byte[] ReadFromResS(AssetBundleFile bundle, string resSName, long offset, int size)
    {
        int idx = FindDirIndex(bundle, resSName);
        byte[] blob = bundle.DataReader.ReadBytes((int)bundle.BlockAndDirInfo.DirectoryInfos[idx].Offset,
                                                    (int)bundle.BlockAndDirInfo.DirectoryInfos[idx].Size);
        var result = new byte[size];
        Buffer.BlockCopy(blob, (int)offset, result, 0, size);
        return result;
    }

    private static long AppendToResS(AssetBundleFile bundle, string resSName, byte[] data)
    {
        int idx = FindDirIndex(bundle, resSName);
        var entry = bundle.BlockAndDirInfo.DirectoryInfos[idx];
        long newOffset = entry.Size; // append at current end
        bundle.AppendToNode(idx, data); // conceptual — mirrors your disk-backed .resS append
        entry.Size += data.Length;
        return newOffset;
    }

    private static void ReplaceBundleEntry(AssetBundleFile bundle, int dirIndex, byte[] newData) =>
        bundle.ReplaceNode(dirIndex, newData); // conceptual — see UnityBundleCAB writer notes

    private static int FindDirIndex(AssetBundleFile bundle, string name)
    {
        for (int i = 0; i < bundle.BlockAndDirInfo.DirectoryInfos.Count; i++)
            if (bundle.BlockAndDirInfo.DirectoryInfos[i].Name == name)
                return i;
        throw new FileNotFoundException($".resS node '{name}' not found in bundle");
    }
}

/// <summary>Placeholder disposable — swap for real pooling if the decoder needs it.</summary>
internal sealed class AutoreleaseScope : IDisposable
{
    public void Dispose() { }
}
