// BundleDoctor/TransplantMode.cs
//
// `BundleDoctor transplant <original.bundle> <modded.bundle> <output.bundle> [options]`
//
// Why this exists (see Program.cs's header comment for the mode this
// replaces): decoding every desktop-authored asset and re-encoding/retargeting
// the whole modded bundle turned out to be unsustainable once Material assets
// were confirmed to also carry platform-specific data, on top of the Shader
// problem Program.cs's --original restore pass already worked around. Rather
// than chase every asset type that turns out to be platform-sensitive, this
// mode never touches the modded bundle's Shaders or Materials AT ALL: it
// starts from the ORIGINAL bundle (already correctly built and platformed for
// iOS) and only ever surgically overwrites the two asset kinds we actually
// want from the mod:
//
//   - Sprite      : raw byte-for-byte transplant. Sprite objects (rect, pivot,
//                   border, the PPtr to their backing Texture2D, physics
//                   outline, etc.) carry no platform-compiled data the way
//                   Shader/Material do, so an untouched byte copy from modded
//                   into original is safe whenever a Sprite's PathId either
//                   differs in bytes from its original counterpart, or has no
//                   counterpart in original at all.
//
//   - Texture2D   : NOT a raw byte copy - the modded bundle's Texture2D
//                   objects are desktop-formatted (RGB24/DXT1/DXT5/
//                   DXT5Crunched typically) and the original's are iOS-
//                   formatted (typically ASTC 4x4/6x6, sometimes ETC2), so a
//                   changed texture still needs a real decode+resample+
//                   re-encode pass. Which textures NEED that pass is decided
//                   by a cheap structural signal instead of a pixel diff: a
//                   Texture2D the mod pipeline never touched stays streamed
//                   out to the bundle's companion .resS exactly like the
//                   original build shipped it, so its own serialized
//                   AssetFileInfo.ByteSize sits at a couple hundred bytes -
//                   just the m_StreamData pointer, no pixels. A Texture2D the
//                   mod DID replace gets its raw pixel bytes serialized
//                   directly into the object (no .resS round trip), which
//                   pushes that same ByteSize into the hundreds-of-KB+ range.
//                   That gap - confirmed via UABEA byte-size inspection
//                   across real modded/original dumps - is readable straight
//                   off AssetFileInfo before any typetree parse, decode, or
//                   pixel comparison, so only the (small) inlined subset ever
//                   pays the decode+resample+re-encode cost; everything still
//                   streamed is left completely byte-for-byte alone. This
//                   replaces an earlier pixel-content diff (decode both sides
//                   to RGBA32, downsample to a grid, score the difference)
//                   that was both the actual runtime bottleneck - the
//                   original/iOS-side ASTC decode has no SIMD path - and a
//                   source of its own false-positive/false-negative
//                   headaches trying to separate "real edit" from cross-codec
//                   quantization noise. The inline-vs-streamed signal sidesteps
//                   both problems: it's a property of how the mod pipeline
//                   itself serializes a replaced texture, not an inference
//                   drawn from comparing pixels.
//
// Matching key: PathId within same-named SerializedFile, exactly like
// Program.cs's existing Shader-restore pass already relies on (same build
// pipeline / same underlying asset database on both platforms -> deterministic
// PathIds across the desktop and iOS builds of the same content). If that
// assumption ever stops holding for a given game/project, this whole matching
// strategy needs to change to name-based matching instead - PathId collisions
// are checked for and logged, never silently overwritten.
//
// Whole SerializedFiles present in only one bundle are logged and skipped -
// inserting an entirely new SerializedFile into the bundle's own directory
// table is a bigger structural change than this pass makes and isn't
// implemented here.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

internal static class TransplantMode
{
    private const int DefaultNewTextureFormat = TextureCodec.FmtASTC_RGBA_4x4;

    // A streamed (untouched) Texture2D only carries its m_StreamData pointer
    // inline - a few hundred bytes of header/metadata, the actual pixels
    // living in the bundle's companion .resS, entirely outside this object's
    // own byte range. A Texture2D the mod pipeline actually replaced gets its
    // raw pixel bytes serialized directly INTO the object (no .resS round
    // trip), which is what pushes AssetFileInfo.ByteSize up into the
    // hundreds-of-KB/multi-MB range. That gap is enormous - confirmed via
    // UABEA byte-size inspection across real modded/original dumps - and is
    // readable straight off AssetFileInfo before any typetree parse or
    // FillPictureData call, so it lets Phase 1 skip the decode+diff pipeline
    // entirely for every texture the mod never touched, instead of paying a
    // full original-side ASTC decode (pure C#, no SIMD - the actual cost
    // behind multi-minute-per-texture stalls on textures with no mip chain
    // to slice from) just to conclude "unchanged" like today. 8KB sits
    // comfortably above the ~200-300 byte streamed footprint and comfortably
    // below any real inlined pixel payload, but this is a starting point:
    // run --dry-run first and check the logged skip count/sizes against
    // what UABEA shows for your own bundle before trusting it on a real
    // transplant.
    private const long DefaultInlineByteSizeThreshold = 8192;

    private sealed class Options
    {
        public string OriginalPath = "";
        public string ModdedPath = "";
        public string OutputPath = "";
        public string? TpkPath;
        public bool DryRun;
        public int NewTextureFormat = DefaultNewTextureFormat;
        public long InlineByteSizeThreshold = DefaultInlineByteSizeThreshold;
    }

    public static int Run(string[] modeArgs)
    {
        Options? opt = ParseArgs(modeArgs);
        if (opt == null)
        {
            Console.Error.WriteLine(
                "usage: BundleDoctor transplant <original.bundle> <modded.bundle> <output.bundle> " +
                "[--inline-size-threshold BYTES] [--dry-run] " +
                "[--new-texture-format FMT] [classdata.tpk]");
            return 2;
        }

        Console.WriteLine(
            $"[config] inline-size-threshold={opt.InlineByteSizeThreshold:N0}B dry-run={opt.DryRun} " +
            $"new-texture-format={TextureCodec.FormatName(opt.NewTextureFormat)}");

        var originalManager = new AssetsManager();
        var moddedManager = new AssetsManager();

        if (opt.TpkPath != null)
        {
            originalManager.LoadClassPackage(opt.TpkPath);
            moddedManager.LoadClassPackage(opt.TpkPath);
        }

        string? tempOriginalUnpacked = null;
        string? tempModdedUnpacked = null;

        int spritesOverridden = 0, spritesAdded = 0, spritesUnchanged = 0;
        int texturesReencoded = 0, texturesUnchanged = 0, texturesSkippedErrors = 0;
        int texturesAddedNew = 0;
        int touchedFiles = 0;

        try
        {
            BundleFileInstance originalBunInst = LoadFullyUnpacked(
                originalManager, opt.OriginalPath, "BundleDoctor-transplant-orig", out tempOriginalUnpacked);
            BundleFileInstance moddedBunInst = LoadFullyUnpacked(
                moddedManager, opt.ModdedPath, "BundleDoctor-transplant-modded", out tempModdedUnpacked);

            // Index the modded bundle's SerializedFiles by name up front so the
            // main loop below (which walks the ORIGINAL's directory, since
            // that's the file we're rebuilding) can look each one up.
            var moddedIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < moddedBunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                string name = moddedBunInst.file.BlockAndDirInfo.DirectoryInfos[i].Name;
                if (LooksLikeSerializedFile(name))
                    moddedIndexByName[name] = i;
            }

            var originalNames = new HashSet<string>(StringComparer.Ordinal);

            for (int dirIndex = 0; dirIndex < originalBunInst.file.BlockAndDirInfo.DirectoryInfos.Count; dirIndex++)
            {
                var origDirInfo = originalBunInst.file.BlockAndDirInfo.DirectoryInfos[dirIndex];
                if (!LooksLikeSerializedFile(origDirInfo.Name))
                    continue;

                originalNames.Add(origDirInfo.Name);

                if (!moddedIndexByName.TryGetValue(origDirInfo.Name, out int moddedDirIndex))
                {
                    Console.WriteLine(
                        $"[{origDirInfo.Name}] no counterpart in modded bundle; left untouched.");
                    continue;
                }

                AssetsFileInstance origAfileInst;
                AssetsFileInstance moddedAfileInst;
                try
                {
                    origAfileInst = originalManager.LoadAssetsFileFromBundle(originalBunInst, dirIndex, loadDeps: false);
                    moddedAfileInst = moddedManager.LoadAssetsFileFromBundle(moddedBunInst, moddedDirIndex, loadDeps: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{origDirInfo.Name}] skipped: could not load as a SerializedFile on one side ({ex.GetType().Name}: {ex.Message}).");
                    continue;
                }

                if (origAfileInst?.file == null || moddedAfileInst?.file == null)
                {
                    Console.WriteLine($"[{origDirInfo.Name}] skipped: not a readable SerializedFile on one side.");
                    continue;
                }

                if (opt.TpkPath != null)
                {
                    originalManager.LoadClassDatabaseFromPackage(origAfileInst.file.Metadata.UnityVersion);
                    moddedManager.LoadClassDatabaseFromPackage(moddedAfileInst.file.Metadata.UnityVersion);
                }

                bool fileTouched = false;

                TransplantSprites(
                    origAfileInst, moddedAfileInst, origDirInfo.Name, opt,
                    ref spritesOverridden, ref spritesAdded, ref spritesUnchanged, ref fileTouched);

                TransplantTextures(
                    originalManager, moddedManager, origAfileInst, moddedAfileInst, origDirInfo.Name, opt,
                    ref texturesReencoded, ref texturesUnchanged, ref texturesSkippedErrors, ref texturesAddedNew,
                    ref fileTouched);

                if (fileTouched && !opt.DryRun)
                {
                    origDirInfo.SetNewData(origAfileInst.file);
                    touchedFiles++;
                }
                else if (fileTouched)
                {
                    touchedFiles++; // count it for the dry-run summary even though nothing is written
                }
            }

            // Log modded-only SerializedFiles (present in modded, absent from
            // original) - not merged, since that would mean inserting a whole
            // new directory entry rather than editing an existing one.
            foreach (var name in moddedIndexByName.Keys)
            {
                if (!originalNames.Contains(name))
                {
                    Console.WriteLine(
                        $"[{name}] exists only in the modded bundle; no original counterpart to transplant " +
                        "into - skipped (would require inserting a whole new SerializedFile).");
                }
            }

            Console.WriteLine(
                $"[summary] Sprites: {spritesOverridden} overridden, {spritesAdded} added, " +
                $"{spritesUnchanged} unchanged. Textures: {texturesReencoded} re-encoded, " +
                $"{texturesAddedNew} added new, {texturesUnchanged} unchanged, " +
                $"{texturesSkippedErrors} skipped due to errors. {touchedFiles} SerializedFile(s) touched.");

            if (opt.DryRun)
            {
                Console.WriteLine("[dry-run] no output written.");
                return 0;
            }

            WritePackedOutput(originalBunInst, opt.OutputPath);
            Console.WriteLine($"Wrote {opt.OutputPath}.");
            return 0;
        }
        finally
        {
            originalManager.UnloadAll();
            moddedManager.UnloadAll();
            if (tempOriginalUnpacked != null) TryDelete(tempOriginalUnpacked);
            if (tempModdedUnpacked != null) TryDelete(tempModdedUnpacked);
        }
    }

    // --- Sprite pass: raw byte-for-byte, no typetree parsing needed at all ---
    private static void TransplantSprites(
        AssetsFileInstance origAfileInst,
        AssetsFileInstance moddedAfileInst,
        string fileName,
        Options opt,
        ref int overridden,
        ref int added,
        ref int unchanged,
        ref bool fileTouched)
    {
        AssetsFile origAf = origAfileInst.file;
        AssetsFile moddedAf = moddedAfileInst.file;

        var origByPathId = new Dictionary<long, AssetFileInfo>();
        foreach (AssetFileInfo info in origAf.GetAssetsOfType(AssetClassID.Sprite))
            origByPathId[info.PathId] = info;

        var usedPathIds = new HashSet<long>();
        foreach (AssetFileInfo info in origAf.Metadata.AssetInfos)
            usedPathIds.Add(info.PathId);

        foreach (AssetFileInfo moddedInfo in moddedAf.GetAssetsOfType(AssetClassID.Sprite))
        {
            byte[] moddedBytes = ReadRawBytes(moddedAf, moddedInfo);

            if (origByPathId.TryGetValue(moddedInfo.PathId, out AssetFileInfo? origInfo))
            {
                byte[] origBytes = ReadRawBytes(origAf, origInfo);
                if (BytesEqual(origBytes, moddedBytes))
                {
                    unchanged++;
                    continue;
                }

                Console.WriteLine(
                    $"[{fileName}] Sprite PathId {moddedInfo.PathId}: differs from original " +
                    $"({origBytes.Length:N0} -> {moddedBytes.Length:N0} bytes) - overriding.");

                if (!opt.DryRun)
                    origInfo.SetNewData(moddedBytes);
                overridden++;
                fileTouched = true;
            }
            else
            {
                if (usedPathIds.Contains(moddedInfo.PathId))
                {
                    Console.WriteLine(
                        $"[{fileName}] Sprite PathId {moddedInfo.PathId} not present in original as a Sprite, " +
                        "but that PathId is already used by a DIFFERENT object in original - skipping this one " +
                        "rather than risk corrupting an unrelated asset.");
                    continue;
                }

                Console.WriteLine(
                    $"[{fileName}] Sprite PathId {moddedInfo.PathId}: not present in original - adding " +
                    $"({moddedBytes.Length:N0} bytes).");

                if (!opt.DryRun)
                {
                    var newInfo = AssetFileInfo.Create(origAf, moddedInfo.PathId, (int)AssetClassID.Sprite);
                    newInfo.SetNewData(moddedBytes);
                    origAf.Metadata.AddAssetInfo(newInfo);
                    usedPathIds.Add(moddedInfo.PathId);
                }
                added++;
                fileTouched = true;
            }
        }
    }

    // One modded Texture2D's worth of state, threaded through the three
    // phases below. Populated sequentially (Phase 1), decoded/resampled/
    // re-encoded in parallel (Phase 2), then applied+logged sequentially
    // (Phase 3). Every item that makes it into the work list has already
    // been decided as needing a re-encode by the inline-size heuristic in
    // Phase 1 - there's no further "is it actually changed" judgment call
    // left to make on pixel content, so there's no Changed/score field here
    // the way there used to be.
    private sealed class TextureWorkItem
    {
        public string TexName = "";
        public long PathId;
        public AssetTypeValueField ModdedBase = null!;
        public int ModdedFormat, ModdedWidth, ModdedHeight;
        public byte[]? ModdedEncoded;

        public bool HasOriginal;
        public AssetFileInfo? OrigInfo;
        public AssetTypeValueField? OrigBase;
        public int OrigFormat, OrigWidth, OrigHeight;

        public bool PathIdCollision;
        public Exception? Error;

        // Filled in by Phase 2.
        public byte[]? FinalEncoded;
    }

    // --- Texture2D pass -------------------------------------------------------
    //
    // Which textures need re-encoding is decided by the inline-size
    // heuristic in Phase 1, before any pixel is ever touched (see this
    // file's header comment). There is no pixel-content diff anywhere in
    // this pass anymore - it was both the actual runtime bottleneck (the
    // original/iOS-side ASTC decode has no SIMD path) and unreliable at
    // telling "modder recolored this" apart from ordinary cross-codec
    // quantization noise. Everything that reaches Phase 2 is presumed
    // genuinely changed and gets decoded+resampled+re-encoded unconditionally;
    // everything else was already filtered out in Phase 1 and never allocates
    // a work item at all.
    //
    // Three phases, split specifically along the "touches the shared
    // AssetsManager/file-stream" line vs. "pure in-memory compute" line:
    //
    //   Phase 1 (sequential) - GetBaseField and FillPictureData both read
    //   through AssetsFileInstance's underlying reader/stream position,
    //   which is shared per-instance state - calling these concurrently
    //   from multiple threads would race on that position and risk silently
    //   corrupt reads, not just a crash. So all field/byte extraction stays
    //   single-threaded here. It's comparatively cheap I/O, not the
    //   expensive part. This is also where the inline-size pre-filter runs,
    //   which means the vast majority of textures never reach GetBaseField
    //   at all.
    //
    //   Phase 2 (parallel) - decode the modded side to RGBA32, resample to
    //   the original's dimensions, re-encode into the original's format.
    //   This is pure in-memory compute over each item's own already-
    //   extracted buffers - no shared AssetsManager/file-stream access - so
    //   it's safe to fan out across cores. Per-texture progress is logged
    //   HERE, as each item finishes, not deferred to Phase 3 -
    //   Console.WriteLine/Error are internally synchronized, so concurrent
    //   lines never garble, they can just print out of enumeration order.
    //
    //   Phase 3 (sequential) - apply the results back onto the shared
    //   AssetsFile/AssetFileInfo state; mutation has to be ordered/single-
    //   threaded regardless of when logging happens.
    private static void TransplantTextures(
        AssetsManager originalManager,
        AssetsManager moddedManager,
        AssetsFileInstance origAfileInst,
        AssetsFileInstance moddedAfileInst,
        string fileName,
        Options opt,
        ref int reencoded,
        ref int unchanged,
        ref int skippedErrors,
        ref int addedNew,
        ref bool fileTouched)
    {
        AssetsFile origAf = origAfileInst.file;
        AssetsFile moddedAf = moddedAfileInst.file;

        // Diagnostic-only counters, so a slow run tells us WHERE the time
        // actually went instead of guessing again. Interlocked because
        // Phase 2 runs in Parallel.ForEach - a plain `long +=` would race
        // and lose increments across threads.
        long moddedDecodeTicks = 0, encodeTicks = 0;
        int skippedByInlineSizeHeuristic = 0;

        var origByPathId = new Dictionary<long, AssetFileInfo>();
        foreach (AssetFileInfo info in origAf.GetAssetsOfType(AssetClassID.Texture2D))
            origByPathId[info.PathId] = info;

        var usedPathIds = new HashSet<long>();
        foreach (AssetFileInfo info in origAf.Metadata.AssetInfos)
            usedPathIds.Add(info.PathId);

        // --- Phase 1: sequential extraction -----------------------------
        var items = new List<TextureWorkItem>();
        foreach (AssetFileInfo moddedInfo in moddedAf.GetAssetsOfType(AssetClassID.Texture2D))
        {
            // Fast pre-filter, ahead of even GetBaseField: a Texture2D the mod
            // never touched is still streamed the same way the original build
            // shipped it, so its own serialized ByteSize stays at the tiny
            // header/m_StreamData-pointer footprint (~200-300B). Only textures
            // the mod pipeline actually replaced get their pixel bytes inlined
            // into the object, which is what drives ByteSize up into the
            // hundreds-of-KB+ range - see DefaultInlineByteSizeThreshold's
            // comment. When a counterpart exists in original AND this modded
            // object is still below that line, skip straight to "unchanged"
            // without opening a base field, reading pixel bytes, or running
            // the decoder at all. This is the actual bottleneck cut: the
            // original-side ASTC decode this skips is what the Phase 2 timing
            // comment below already identifies as the multi-minute cost on
            // real runs, not the (cheap, native) modded-side decode.
            if (origByPathId.ContainsKey(moddedInfo.PathId) && moddedInfo.ByteSize <= opt.InlineByteSizeThreshold)
            {
                skippedByInlineSizeHeuristic++;
                unchanged++;
                continue;
            }

            AssetTypeValueField moddedBase = moddedManager.GetBaseField(moddedAfileInst, moddedInfo);
            var item = new TextureWorkItem
            {
                TexName = moddedBase["m_Name"].AsString,
                PathId = moddedInfo.PathId,
                ModdedBase = moddedBase,
            };
            items.Add(item);

            try
            {
                item.ModdedFormat = moddedBase["m_TextureFormat"].AsInt;
                item.ModdedWidth = moddedBase["m_Width"].AsInt;
                item.ModdedHeight = moddedBase["m_Height"].AsInt;
                if (item.ModdedWidth <= 0 || item.ModdedHeight <= 0)
                    throw new InvalidDataException($"invalid dimensions for '{item.TexName}': {item.ModdedWidth}x{item.ModdedHeight}");
                item.ModdedEncoded = ExtractEncodedBytes(moddedAfileInst, moddedBase, item.TexName);

                if (origByPathId.TryGetValue(moddedInfo.PathId, out AssetFileInfo? origInfo))
                {
                    // Only the original's format/dimensions are needed now -
                    // the resample target and the format to re-encode into.
                    // No original-side pixel read/decode at all: the
                    // inline-size pre-filter above already established that
                    // this texture was replaced, so there's nothing left to
                    // diff against.
                    item.HasOriginal = true;
                    item.OrigInfo = origInfo;
                    AssetTypeValueField origBase = originalManager.GetBaseField(origAfileInst, origInfo);
                    item.OrigBase = origBase;
                    item.OrigFormat = origBase["m_TextureFormat"].AsInt;
                    item.OrigWidth = origBase["m_Width"].AsInt;
                    item.OrigHeight = origBase["m_Height"].AsInt;
                    if (item.OrigWidth <= 0 || item.OrigHeight <= 0)
                        throw new InvalidDataException($"invalid dimensions for '{item.TexName}': {item.OrigWidth}x{item.OrigHeight}");
                }
                else if (usedPathIds.Contains(moddedInfo.PathId))
                {
                    item.PathIdCollision = true;
                    Console.WriteLine(
                        $"[{fileName}] Texture2D '{item.TexName}' PathId {item.PathId} not present in " +
                        "original as a Texture2D, but that PathId is already used by a different object - skipping.");
                }
            }
            catch (Exception ex)
            {
                item.Error = ex;
            }
        }

        // --- Phase 2: parallel decode/resample/encode ----------------------
        // Progress logging happens HERE, immediately as each texture
        // finishes, not batched into Phase 3 - Console.WriteLine/Error are
        // internally synchronized (System.IO.SyncTextWriter), so concurrent
        // calls from multiple threads never interleave mid-line into
        // garbled output; the only observable effect is that completed
        // textures may print out of their original enumeration order, which
        // is a fair trade for not going silent on a large bundle for
        // however long the whole batch takes.
        // Explicit rather than relying on Parallel.ForEach's default (which is
        // already ~= ProcessorCount, so this changes nothing today) - mainly
        // so BUNDLEDOCTOR_MAX_PARALLELISM is available to force this down to
        // 1 for an apples-to-apples timing comparison against the parallel
        // path, without needing a rebuild. On the CI runner this actually
        // matters (ubuntu-latest is a 2 vCPU box - see doctor-bundle.yml),
        // so more Task-level parallelism than that just adds scheduling
        // overhead, it doesn't add throughput.
        int maxParallelism = Environment.ProcessorCount;
        string? dopOverride = Environment.GetEnvironmentVariable("BUNDLEDOCTOR_MAX_PARALLELISM");
        if (!string.IsNullOrWhiteSpace(dopOverride) && int.TryParse(dopOverride, out int dop) && dop > 0)
            maxParallelism = dop;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism };

        Parallel.ForEach(items, parallelOptions, item =>
        {
            if (item.Error != null || item.PathIdCollision)
                return;

            try
            {
                if (item.HasOriginal)
                {
                    // Reached Phase 2 at all only because Phase 1's
                    // inline-size heuristic already decided this texture was
                    // replaced by the mod - no original-side decode, no
                    // diff, no threshold to weigh. Decode the modded side,
                    // resample to the original's own dimensions, re-encode
                    // into the original's own format. Kyaru's native decoder
                    // handles the modded (desktop) side - DXT/RGB24/RGBA32/
                    // Crunch - cheap regardless of resolution.
                    var swModded = System.Diagnostics.Stopwatch.StartNew();
                    byte[] moddedRgba = TextureCodec.DecodeToRgba32(
                        item.ModdedEncoded!, item.ModdedWidth, item.ModdedHeight, item.ModdedFormat, item.TexName);
                    swModded.Stop();
                    Interlocked.Add(ref moddedDecodeTicks, swModded.ElapsedTicks);

                    var swEncode = System.Diagnostics.Stopwatch.StartNew();
                    byte[] resampled = TextureCodec.ResampleBilinear(
                        moddedRgba, item.ModdedWidth, item.ModdedHeight, item.OrigWidth, item.OrigHeight);
                    item.FinalEncoded = TextureCodec.EncodeFromRgba32(
                        resampled, item.OrigWidth, item.OrigHeight, item.OrigFormat, item.TexName);
                    swEncode.Stop();
                    Interlocked.Add(ref encodeTicks, swEncode.ElapsedTicks);

                    Console.WriteLine(
                        $"[{fileName}] Texture2D '{item.TexName}' PathId {item.PathId}: " +
                        $"orig {item.OrigWidth}x{item.OrigHeight} {TextureCodec.FormatName(item.OrigFormat)} vs " +
                        $"modded {item.ModdedWidth}x{item.ModdedHeight} {TextureCodec.FormatName(item.ModdedFormat)} " +
                        "-> inlined by mod, re-encoding.");
                }
                else
                {
                    // No counterpart at all: decode the modded texture and encode it
                    // fresh into the configured default new-texture format; Phase 3
                    // inserts it as a brand new object using the SAME baseField
                    // already read from modded (correct field layout for this Unity
                    // build), just with the format/image-data fields swapped.
                    var swEncode = System.Diagnostics.Stopwatch.StartNew();
                    byte[] moddedRgba = TextureCodec.DecodeToRgba32(
                        item.ModdedEncoded!, item.ModdedWidth, item.ModdedHeight, item.ModdedFormat, item.TexName);
                    item.FinalEncoded = TextureCodec.EncodeFromRgba32(
                        moddedRgba, item.ModdedWidth, item.ModdedHeight, opt.NewTextureFormat, item.TexName);
                    swEncode.Stop();
                    Interlocked.Add(ref encodeTicks, swEncode.ElapsedTicks);

                    Console.WriteLine(
                        $"[{fileName}] Texture2D '{item.TexName}' PathId {item.PathId}: not present in original - " +
                        $"adding as {TextureCodec.FormatName(opt.NewTextureFormat)} ({item.ModdedWidth}x{item.ModdedHeight}).");
                }
            }
            catch (Exception ex)
            {
                item.Error = ex;
                Console.Error.WriteLine(
                    $"[{fileName}] Texture2D '{item.TexName}' PathId {item.PathId}: skipped due to error - " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        });

        // --- Phase 3: sequential apply -------------------------------------
        // Pure bookkeeping + the actual mutations now - all the logging
        // already happened in Phase 1/2 as each texture was decided, so
        // there's nothing left to print here.
        foreach (TextureWorkItem item in items)
        {
            if (item.PathIdCollision || item.Error != null)
            {
                skippedErrors++;
                continue;
            }

            if (item.HasOriginal)
            {
                if (!opt.DryRun)
                {
                    AssetTypeValueField origBase = item.OrigBase!;
                    origBase["m_TextureFormat"].AsInt = item.OrigFormat; // unchanged - keep original's own format
                    origBase["m_MipCount"].AsInt = 1;
                    origBase["m_CompleteImageSize"].AsInt = item.FinalEncoded!.Length;

                    AssetTypeValueField streamData = origBase["m_StreamData"];
                    streamData["offset"].AsULong = 0;
                    streamData["size"].AsInt = 0;
                    streamData["path"].AsString = string.Empty;
                    origBase["image data"].AsByteArray = item.FinalEncoded;

                    item.OrigInfo!.SetNewData(origBase);
                }
                reencoded++;
                fileTouched = true;
            }
            else
            {
                if (!opt.DryRun)
                {
                    AssetTypeValueField moddedBase = item.ModdedBase;
                    moddedBase["m_TextureFormat"].AsInt = opt.NewTextureFormat;
                    moddedBase["m_MipCount"].AsInt = 1;
                    moddedBase["m_CompleteImageSize"].AsInt = item.FinalEncoded!.Length;

                    AssetTypeValueField streamData = moddedBase["m_StreamData"];
                    streamData["offset"].AsULong = 0;
                    streamData["size"].AsInt = 0;
                    streamData["path"].AsString = string.Empty;
                    moddedBase["image data"].AsByteArray = item.FinalEncoded;

                    var newInfo = AssetFileInfo.Create(origAf, item.PathId, (int)AssetClassID.Texture2D);
                    newInfo.SetNewData(moddedBase);
                    origAf.Metadata.AddAssetInfo(newInfo);
                    usedPathIds.Add(item.PathId);
                }
                addedNew++;
                fileTouched = true;
            }
        }

        if (items.Count > 0 || skippedByInlineSizeHeuristic > 0)
        {
            double moddedMs = moddedDecodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double encodeMs = encodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine(
                $"[{fileName}] texture timing (summed across all threads, so this can exceed wall time by " +
                $"~{maxParallelism}x): modded-side decode={moddedMs:N0}ms, re-encode={encodeMs:N0}ms across " +
                $"{reencoded + addedNew} changed/new textures. {skippedByInlineSizeHeuristic} texture(s) never " +
                $"decoded at all - still streamed (<= {opt.InlineByteSizeThreshold:N0}B, presumed untouched by " +
                $"the mod). Parallelism cap: {maxParallelism} (set BUNDLEDOCTOR_MAX_PARALLELISM to override).");
        }
    }

    // Sequential-only: reads through AssetsFileInstance's shared reader/
    // stream position (TextureFile.ReadTextureFile + FillPictureData), so
    // this must stay in Phase 1, never called from the parallel Phase 2.
    private static byte[] ExtractEncodedBytes(
        AssetsFileInstance afileInst, AssetTypeValueField baseField, string texName)
    {
        TextureFile tf = TextureFile.ReadTextureFile(baseField);
        return tf.FillPictureData(afileInst)
            ?? throw new InvalidDataException($"could not load texture data for '{texName}'");
    }

    // --- shared plumbing -----------------------------------------------------

    private static byte[] ReadRawBytes(AssetsFile af, AssetFileInfo info)
    {
        AssetsFileReader reader = af.Reader;
        reader.Position = info.GetAbsoluteByteOffset(af);
        return reader.ReadBytes((int)info.ByteSize);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return ((ReadOnlySpan<byte>)a).SequenceEqual(b);
    }

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);

    // Same "materialize a genuinely uncompressed bundle first" idiom Program.cs
    // uses for its input bundle - kept here as its own helper since this mode
    // needs it for BOTH bundles (original and modded), not just one.
    private static BundleFileInstance LoadFullyUnpacked(
        AssetsManager manager, string path, string tempPrefix, out string? tempPath)
    {
        tempPath = null;
        BundleFileInstance loaded = manager.LoadBundleFile(path, unpackIfPacked: false);

        AssetBundleCompressionType compression = loaded.file.GetCompressionType();
        if (compression == AssetBundleCompressionType.None)
        {
            Console.WriteLine($"[bundle] '{path}' is already uncompressed.");
            return loaded;
        }

        string unpackedPath = Path.Combine(Path.GetTempPath(), $"{tempPrefix}-{Guid.NewGuid():N}.unity3d");
        using (var unpackedStream = File.Create(unpackedPath))
        using (var unpackedWriter = new AssetsFileWriter(unpackedStream))
        {
            loaded.file.Unpack(unpackedWriter);
        }

        manager.UnloadBundleFile(loaded);
        tempPath = unpackedPath;

        BundleFileInstance reloaded = manager.LoadBundleFile(unpackedPath, unpackIfPacked: false);
        if (reloaded.file.GetCompressionType() != AssetBundleCompressionType.None || reloaded.file.DataIsCompressed)
            throw new InvalidDataException($"failed to fully decompress '{path}'");

        Console.WriteLine($"[bundle] '{path}' decompressed ({compression} -> None).");
        return reloaded;
    }

    // Same three-stage Write -> Pack -> transcode-to-real-LZ4 pipeline
    // Program.cs's Main uses for its own output, reused verbatim here since
    // the container-format concerns (Pack() mislabeling HC as LZ4) apply
    // identically regardless of which direction produced the working bundle.
    private static void WritePackedOutput(BundleFileInstance bunInst, string outputPath)
    {
        string tempUnpackedPath = Path.Combine(Path.GetTempPath(), $"BundleDoctor-transplant-{Guid.NewGuid():N}.unity3d");
        string tempPackedPath = Path.Combine(Path.GetTempPath(), $"BundleDoctor-transplant-packed-{Guid.NewGuid():N}.unity3d");

        try
        {
            using (var tempStream = File.Create(tempUnpackedPath))
            using (var tempWriter = new AssetsFileWriter(tempStream))
            {
                bunInst.file.Write(tempWriter, 0);
            }

            var packManager = new AssetsManager();
            try
            {
                BundleFileInstance materializedInst = packManager.LoadBundleFile(tempUnpackedPath, unpackIfPacked: false);
                using var packedStream = File.Create(tempPackedPath);
                using var packedWriter = new AssetsFileWriter(packedStream);
                materializedInst.file.Pack(packedWriter, AssetBundleCompressionType.LZ4);
            }
            finally
            {
                packManager.UnloadAll();
            }

            byte[] packedBytes = File.ReadAllBytes(tempPackedPath);
            byte[] forcedLz4Bytes = UnityFsLz4Transcoder.ForceStandardLz4(packedBytes);
            File.WriteAllBytes(outputPath, forcedLz4Bytes);
        }
        finally
        {
            TryDelete(tempUnpackedPath);
            TryDelete(tempPackedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static Options? ParseArgs(string[] args)
    {
        var positional = new List<string>(args);
        var opt = new Options();

        for (int i = 0; i < positional.Count; i++)
        {
            if (string.Equals(positional[i], "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                opt.DryRun = true;
                positional.RemoveAt(i);
                i--;
            }
            else if (string.Equals(positional[i], "--new-texture-format", StringComparison.OrdinalIgnoreCase) && i + 1 < positional.Count)
            {
                opt.NewTextureFormat = ParseFormatName(positional[i + 1]);
                positional.RemoveRange(i, 2);
                i--;
            }
            else if (string.Equals(positional[i], "--inline-size-threshold", StringComparison.OrdinalIgnoreCase) && i + 1 < positional.Count)
            {
                if (!long.TryParse(positional[i + 1], out long b) || b < 0)
                    return null;
                opt.InlineByteSizeThreshold = b;
                positional.RemoveRange(i, 2);
                i--;
            }
        }

        if (positional.Count < 3)
            return null;

        opt.OriginalPath = positional[0];
        opt.ModdedPath = positional[1];
        opt.OutputPath = positional[2];
        if (positional.Count >= 4)
            opt.TpkPath = positional[3];

        return opt;
    }

    private static int ParseFormatName(string value) => value.Trim().ToUpperInvariant() switch
    {
        "RGBA32" => TextureCodec.FmtRGBA32,
        "ASTC_RGBA_4X4" => TextureCodec.FmtASTC_RGBA_4x4,
        "ASTC_RGBA_6X6" => TextureCodec.FmtASTC_RGBA_6x6,
        "ASTC_8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ASTC_RGBA_8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ASTC8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ETC2" => TextureCodec.FmtETC2_RGBA8,
        "ETC2_RGBA8" => TextureCodec.FmtETC2_RGBA8,
        "ETC2_RGB" => TextureCodec.FmtETC2_RGB,
        _ => throw new ArgumentException(
            $"Unknown --new-texture-format '{value}'. Use RGBA32, ETC2, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, ASTC_RGBA_6x6, or ASTC_RGBA_8x8.")
    };
}
