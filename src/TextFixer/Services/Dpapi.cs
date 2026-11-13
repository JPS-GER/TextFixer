using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TextFixer.Services;

/// <summary>Windows DPAPI (current-user scope) via crypt32, without extra package references.</summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "TextFixer.ChatGptCredentials.v1"u8.ToArray();

    public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob { cbData = input.Length, pbData = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                {
                    if (!protect)
                    {
                        Marshal.Copy(new byte[output.cbData], 0, output.pbData, output.cbData);
                    }

                    LocalFree(output.pbData);
                }
            }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
