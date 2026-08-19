using System;
using System.Runtime.InteropServices;
using System.IO;

internal static class NativeEtc2
{
    private const string LibraryName = "bundle_doctor_etc2";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int etc2_encode_rgba8(
        byte[] rgba,
        uint width,
        uint height,
        [MarshalAs(UnmanagedType.I1)] bool alpha,
        float effort,
        uint jobs,
        out IntPtr encoded,
        out uint encodedLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void etc2_free(IntPtr buffer);

    public static byte[] Encode(byte[] rgba, int width, int height, bool alpha, float effort, int jobs)
    {
        if (rgba is null)
            throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException($"{width}x{height}");

        int expected = checked(width * height * 4);
        if (rgba.Length != expected)
            throw new ArgumentException($"RGBA buffer length {rgba.Length} != expected {expected}", nameof(rgba));

        int status = etc2_encode_rgba8(
            rgba,
            checked((uint)width),
            checked((uint)height),
            alpha,
            effort,
            checked((uint)Math.Max(1, jobs)),
            out IntPtr encoded,
            out uint encodedLength);

        if (status != 0 || encoded == IntPtr.Zero || encodedLength == 0)
            throw new InvalidDataException($"native ETC2 encoder failed (status {status})");

        try
        {
            if (encodedLength > int.MaxValue)
                throw new InvalidDataException("native ETC2 encoder returned an impossibly large buffer");

            var result = new byte[(int)encodedLength];
            Marshal.Copy(encoded, result, 0, result.Length);
            return result;
        }
        finally
        {
            etc2_free(encoded);
        }
    }
}
