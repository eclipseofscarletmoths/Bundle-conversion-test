// BundleDoctor/Program.cs
//
// Doctors a Limbus Company DESKTOP asset bundle into an iOS-loadable one.
//   - retargets SerializedFile m_TargetPlatform 19 (StandaloneWindows64) -> 9 (iOS)
//   - re-encodes any Texture2D whose format iOS can't sample (DXT1/DXT5/DXT5Crunched/RGB24)
//     into RGBA32, leaving already-iOS-safe formats (RGBA32, ASTC 4x4/6x6) untouched
//   - decoding is done by AssetsTools.NET.Texture itself (it ports detex's DXT1/DXT5/BC7/
//     ETC1/ETC2 decoders), so no external Texture2DDecoder dependency is needed
//   - converted textures are moved OUT of .resS and into inline "image data" so we never
//     have to hand-roll .resS byte-offset patching inside a bundle container. That hand-rolled
//     path (ReadFromResS/AppendToResS/AppendToNode/ReplaceNode in the previous draft) doesn't
//     correspond to any real AssetsTools.NET API and is almost certainly why earlier attempts
//     produced corrupted bundles -- those methods don't exist on AssetBundleFile.
//
// Deps (NuGet):
//   AssetsTools.NET          - core read/write, replacers
//   AssetsTools.NET.Texture  - TextureFile helper (decode DXT1/DXT5/DXT5Crunched/RGB24/etc.)
//
// Usage: BundleDoctor <input.bundle> <output.bundle> [classdata.tpk]
//
// The classdata.tpk arg is optional. AssetBundles built by Unity normally embed their own
// type trees, so GetBaseField usually works without one. If you hit a "type template not
// found" / mono type exception, download a matching tpk from the AssetRipper/Tpk repo, check
// it into the repo, and pass its path as the 3rd arg -- LoadClassPackage + LoadClassDatabase-
// FromPackage(af.Metadata.UnityVersion) get called automatically when it's supplied.

using System;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

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
            Console.Error.WriteLine("usage: BundleDoctor <input.bundle> <output.bundle> [classdata.tpk]");
            return 2;
        }

        string inputPath = args[0];
        string outputPath = args[1];
        string? tpkPath = args.Length > 2 ? args[2] : null;

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
                    continue; // already RGBA32/ASTC -- leave completely untouched

                int width = baseField["m_Width"].AsInt;
                int height = baseField["m_Height"].AsInt;
                string texName = baseField["m_Name"].AsString;

                // AssetsTools.NET.Texture's current API separates reading the encoded bytes
                // from decoding them. The older GetTextureData(AssetsFileInstance) helper used
                // by some wiki examples is not present in the package used by this project.
                // FillPictureData resolves inline data or a streamed .resS entry, then the
                // managed decoder converts the encoded texture into BGRA32.
                TextureFile tf = TextureFile.ReadTextureFile(baseField);

                byte[] encodedData = tf.FillPictureData(afileInst)
                    ?? throw new InvalidDataException($"could not load texture data for '{texName}'");

                byte[] bgra32 = TextureFile.DecodeManagedData(
                    encodedData,
                    (TextureFormat)format,
                    width,
                    height,
                    useBgra: true)
                    ?? throw new InvalidDataException($"could not decode texture data for '{texName}'");

                if (bgra32.Length != width * height * 4)
                    throw new InvalidDataException(
                        $"decoded size mismatch for '{texName}': got {bgra32.Length}, " +
                        $"expected {width * height * 4}");

                byte[] rgba32 = SwapRedAndBlue(bgra32);

                // --- 3. Write RGBA32 back into the Texture2D --------------------------
                // AssetsTools.NET.Texture 3.0.2 exposes the decoding helpers we use above,
                // but does not expose the newer 5-argument SetPictureData overload. Keep the
                // write-back entirely type-tree based instead. This is also preferable here
                // because it lets us explicitly move the pixels inline and clear any .resS
                // streaming reference.
                baseField["m_TextureFormat"].AsInt = kFmtRGBA32;
                baseField["m_MipCount"].AsInt = 1;
                baseField["m_CompleteImageSize"].AsInt = rgba32.Length;

                AssetTypeValueField streamData = baseField["m_StreamData"];
                streamData["offset"].AsULong = 0;
                streamData["size"].AsInt = 0;
                streamData["path"].AsString = string.Empty;
                baseField["image data"].AsByteArray = rgba32;

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

    // AssetsTools.NET.Texture decodes to BGRA32; Unity's RGBA32 wants R and B swapped back.
    private static byte[] SwapRedAndBlue(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2]; // R <- B
            rgba[i + 1] = bgra[i + 1]; // G
            rgba[i + 2] = bgra[i + 0]; // B <- R
            rgba[i + 3] = bgra[i + 3]; // A
        }
        return rgba;
    }

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
}
