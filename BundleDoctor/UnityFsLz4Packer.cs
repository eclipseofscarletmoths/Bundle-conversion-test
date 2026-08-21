// BundleDoctor/UnityFsLz4Packer.cs
//
// Takes a fully uncompressed UnityFS bundle (as produced by
// AssetBundleFile.Write()) and re-emits it compressed with GENUINE
// standard/fast LZ4 - not via AssetsTools.NET's own AssetBundleFile.Pack(),
// which routes block compression through its Encode32HC path regardless of
// whether AssetBundleCompressionType.LZ4 or .LZ4HC is requested (see
// Program.cs's call site comment - the enum there only changed the declared
// compression-type byte, not the actual encoder). Limbus Company's own
// asset loader has trouble with the resulting HC-encoded blocks, which is
// the whole reason this file exists: LZ4Codec.Encode(..., LZ4Level.L00_FAST)
// below is K4os's real greedy/fast LZ4 encoder, the same family of encoder
// Unity itself uses for its own LZ4-compressed (non-HC) bundles.
//
// This mirrors ZSingularity's own UnityBundleCAB.m byte-for-byte, including
// its 0x200 (BlockInfoNeedPaddingAtStart) fix - see that file's header/
// blocks-info comments for the full UnityFS format writeup this is ported
// from. Keeping both sides of the pipeline byte-identical in how they frame
// the archive is the point: whatever this repo writes, the tweak's own
// reader (and any other spec-following UnityFS reader, like AssetsTools.NET
// itself on the verification reload below) needs to parse the same way.
//
// Every multi-byte integer in the UnityFS header/blocks-info is big-endian;
// this file uses BinaryPrimitives explicitly throughout rather than relying
// on BitConverter, since BitConverter's endianness is platform-dependent.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

internal static class UnityFsLz4Packer
{
    // UnityFS compression-type values (low 6 bits of the archive flags,
    // and of each per-block flags entry) - 0 = none, 2 = LZ4, 3 = LZ4HC.
    // Both 2 and 3 decode identically (same block bitstream); only the
    // encoder differs. We always declare 2 here because we always used
    // the real fast encoder to produce these bytes.
    private const uint CompressionTypeLz4 = 2;

    private sealed class NodeEntry
    {
        public long Offset;
        public long Size;
        public uint Flags;
        public string Path = "";
    }

    /// <summary>
    /// Parses <paramref name="uncompressed"/> (a compression-type-0 UnityFS
    /// bundle) and returns a new UnityFS archive with the same header/node
    /// table, whose single data block has been compressed with standard
    /// (fast) LZ4.
    /// </summary>
    public static byte[] PackAsStandardLz4(byte[] uncompressed)
    {
        int pos = 0;
        ReadSignature(uncompressed, ref pos);
        _ = ReadU32(uncompressed, ref pos); // format version - not needed, we always write 8 below
        string unityVersion = ReadCString(uncompressed, ref pos);
        string unityRevision = ReadCString(uncompressed, ref pos);
        _ = ReadI64(uncompressed, ref pos); // archive size - recomputed on write
        uint compressedBlocksInfoSize = ReadU32(uncompressed, ref pos);
        uint uncompressedBlocksInfoSize = ReadU32(uncompressed, ref pos);
        uint flags = ReadU32(uncompressed, ref pos);

        bool blocksInfoAtEnd = (flags & 0x80) != 0;
        uint compressionType = flags & 0x3Fu;
        if (compressionType != 0)
        {
            throw new InvalidDataException(
                $"UnityFsLz4Packer expected an uncompressed input bundle (compression=0), " +
                $"got compression type {compressionType}. The Write() step upstream of this " +
                "call is supposed to guarantee an uncompressed materialized bundle.");
        }

        // Unconditional 16-byte alignment from file start, same as ZSingularity's
        // UnityBundleCAB.m ubc_parse_header - see that function's comment for why
        // this isn't gated on flags bit 9 (0x200) when reading.
        pos = AlignTo16(pos);

        int blocksInfoStart = blocksInfoAtEnd
            ? uncompressed.Length - (int)compressedBlocksInfoSize
            : pos;

        if (blocksInfoStart < 0 || (long)blocksInfoStart + compressedBlocksInfoSize > uncompressed.Length)
            throw new InvalidDataException("Materialized bundle's blocks-info span is out of range.");

        // compression=0, so compressed/uncompressed sizes should already match -
        // read the smaller of the two rather than trusting either blindly, same
        // defensive read UnityBundleCAB.m's ubc_extract_blocks_info uses.
        int blocksInfoLen = (int)Math.Min(compressedBlocksInfoSize, uncompressedBlocksInfoSize);
        byte[] blocksInfo = new byte[blocksInfoLen];
        Array.Copy(uncompressed, blocksInfoStart, blocksInfo, 0, blocksInfoLen);

        List<NodeEntry> nodes = ParseBlocksInfoNodes(blocksInfo);

        int dataStart = blocksInfoAtEnd ? pos : blocksInfoStart + (int)compressedBlocksInfoSize;
        dataStart = AlignTo16(dataStart);

        int dataLen = uncompressed.Length - dataStart;
        if (dataLen < 0)
            throw new InvalidDataException("Materialized bundle's data span is out of range.");

        byte[] data = new byte[dataLen];
        Array.Copy(uncompressed, dataStart, data, 0, dataLen);

        byte[] compressedData = Lz4EncodeBlock(data);

        return BuildArchive(unityVersion, unityRevision, data.Length, compressedData, nodes);
    }

    private static List<NodeEntry> ParseBlocksInfoNodes(byte[] blocksInfo)
    {
        int pos = 16; // uncompressed-data hash, unused here
        uint blockCount = ReadU32(blocksInfo, ref pos);

        // Skip the per-block StorageBlock entries (uSize u32, cSize u32, flags u16
        // each) - we only need the node table below; the single re-compressed
        // block BuildArchive emits replaces these entirely.
        for (uint i = 0; i < blockCount; i++)
        {
            pos += 4 + 4 + 2;
            if (pos > blocksInfo.Length)
                throw new InvalidDataException("Malformed blocks-info (StorageBlock entries).");
        }

        uint nodeCount = ReadU32(blocksInfo, ref pos);
        var nodes = new List<NodeEntry>((int)nodeCount);
        for (uint i = 0; i < nodeCount; i++)
        {
            long offset = ReadI64(blocksInfo, ref pos);
            long size = ReadI64(blocksInfo, ref pos);
            uint entryFlags = ReadU32(blocksInfo, ref pos);
            string path = ReadCString(blocksInfo, ref pos);
            nodes.Add(new NodeEntry { Offset = offset, Size = size, Flags = entryFlags, Path = path });
        }

        if (nodes.Count == 0)
            throw new InvalidDataException("Materialized bundle has no directory nodes.");
        return nodes;
    }

    private static byte[] BuildArchive(string unityVersion, string unityRevision,
                                        long uncompressedDataLength, byte[] compressedData,
                                        List<NodeEntry> nodes)
    {
        using var blocksInfo = new MemoryStream();
        blocksInfo.Write(new byte[16], 0, 16); // zero hash - matches UnityBundleCAB.m's writer
        WriteU32(blocksInfo, 1); // block count
        WriteU32(blocksInfo, (uint)uncompressedDataLength);
        WriteU32(blocksInfo, (uint)compressedData.Length);
        WriteU16(blocksInfo, (ushort)CompressionTypeLz4);
        WriteU32(blocksInfo, (uint)nodes.Count);
        foreach (NodeEntry node in nodes)
        {
            WriteI64(blocksInfo, node.Offset);
            WriteI64(blocksInfo, node.Size);
            WriteU32(blocksInfo, node.Flags);
            WriteCString(blocksInfo, node.Path);
        }

        byte[] blocksInfoBytes = blocksInfo.ToArray();
        byte[] compressedBlocksInfo = Lz4EncodeBlock(blocksInfoBytes);

        using var outStream = new MemoryStream();
        WriteBytes(outStream, Encoding.ASCII.GetBytes("UnityFS\0"));
        WriteU32(outStream, 8); // format version
        WriteCString(outStream, unityVersion);
        WriteCString(outStream, unityRevision);
        long archiveSizeOffset = outStream.Position;
        WriteI64(outStream, 0); // placeholder, patched below once the final length is known
        WriteU32(outStream, (uint)compressedBlocksInfo.Length);
        WriteU32(outStream, (uint)blocksInfoBytes.Length);
        // 0x40 = BlocksAndDirectoryInfoCombined, 0x200 = BlockInfoNeedPaddingAtStart.
        // We always pad to a 16-byte boundary below, so this must say so - same fix
        // as ZSingularity's UnityBundleCAB.m ubc_write_lz4hc_archive. Without this
        // bit, a spec-following reader won't skip the padding and will feed it into
        // the LZ4 decompressor as real stream bytes.
        WriteU32(outStream, 0x40 | 0x200 | CompressionTypeLz4);

        PadTo16(outStream);
        WriteBytes(outStream, compressedBlocksInfo);
        PadTo16(outStream);
        WriteBytes(outStream, compressedData);

        byte[] result = outStream.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)archiveSizeOffset, 8), result.Length);
        return result;
    }

    // --- Standard (fast) LZ4 block encode ----------------------------------

    private static byte[] Lz4EncodeBlock(byte[] data)
    {
        if (data.Length == 0) return Array.Empty<byte>();

        int bound = LZ4Codec.MaximumOutputSize(data.Length);
        byte[] dst = new byte[bound];
        // L00_FAST is K4os's genuine standard/greedy LZ4 mode - the actual fast
        // encoder, not an optimal-parse "HC" one. Getting real standard-LZ4 bytes
        // out (instead of a relabeled HC stream) is the entire point of this file.
        int written = LZ4Codec.Encode(data, 0, data.Length, dst, 0, dst.Length, LZ4Level.L00_FAST);
        if (written <= 0)
            throw new InvalidDataException("Standard LZ4 compression failed.");

        if (written == dst.Length) return dst;
        byte[] trimmed = new byte[written];
        Array.Copy(dst, trimmed, written);
        return trimmed;
    }

    // --- Bounds-checked big-endian primitives ------------------------------

    private static void ReadSignature(byte[] buf, ref int pos)
    {
        const string sig = "UnityFS\0";
        if (pos + sig.Length > buf.Length ||
            Encoding.ASCII.GetString(buf, pos, sig.Length) != sig)
        {
            throw new InvalidDataException("Materialized bundle is missing the UnityFS signature.");
        }
        pos += sig.Length;
    }

    private static uint ReadU32(byte[] buf, ref int pos)
    {
        if (pos + 4 > buf.Length) throw new InvalidDataException("Truncated materialized bundle (u32).");
        uint v = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos, 4));
        pos += 4;
        return v;
    }

    private static long ReadI64(byte[] buf, ref int pos)
    {
        if (pos + 8 > buf.Length) throw new InvalidDataException("Truncated materialized bundle (i64).");
        long v = BinaryPrimitives.ReadInt64BigEndian(buf.AsSpan(pos, 8));
        pos += 8;
        return v;
    }

    private static string ReadCString(byte[] buf, ref int pos)
    {
        int start = pos;
        while (pos < buf.Length && buf[pos] != 0) pos++;
        if (pos >= buf.Length) throw new InvalidDataException("Unterminated string in materialized bundle.");
        string s = Encoding.UTF8.GetString(buf, start, pos - start);
        pos += 1; // consume the NUL
        return s;
    }

    private static int AlignTo16(int pos)
    {
        int rem = pos % 16;
        return rem == 0 ? pos : pos + (16 - rem);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteI64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteCString(Stream s, string v)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(v);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0);
    }

    private static void WriteBytes(Stream s, byte[] v) => s.Write(v, 0, v.Length);

    private static void PadTo16(Stream s)
    {
        while (s.Position % 16 != 0) s.WriteByte(0);
    }
}
