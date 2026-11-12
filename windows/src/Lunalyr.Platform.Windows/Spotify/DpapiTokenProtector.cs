using System.Runtime.InteropServices;
using System.Text;
using Lunalyr.Core.Player.Spotify;

namespace Lunalyr.Platform.Spotify;

/// <summary>
/// Encrypts the Spotify token with DPAPI for the current Windows user (<c>CryptProtectData</c>, no UI), with
/// Lunalyr's own entropy so another app calling DPAPI on the file gets nothing. Only this one file is touched; no
/// credential store (Credential Manager, browsers) is read or written.
/// </summary>
public sealed partial class DpapiTokenProtector : ITokenProtector
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lunalyr.Spotify.v1");

    public byte[] Protect(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        return Transform(plain, protect: true)
            ?? throw new InvalidOperationException($"DPAPI couldn't encrypt the Spotify token (error {Marshal.GetLastPInvokeError()}).");
    }

    public byte[]? Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        return protectedBytes.Length == 0 ? null : Transform(protectedBytes, protect: false);
    }

    private static unsafe byte[]? Transform(byte[] input, bool protect)
    {
        fixed (byte* data = input)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = (uint)input.Length, Data = data };
            var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = entropy };
            DataBlob outBlob = default;
            var ok = protect
                ? CryptProtectData(&inBlob, null, &entropyBlob, 0, 0, UiForbidden, &outBlob)
                : CryptUnprotectData(&inBlob, 0, &entropyBlob, 0, 0, UiForbidden, &outBlob);
            if (!ok) return null;
            try
            {
                return new ReadOnlySpan<byte>(outBlob.Data, (int)outBlob.Size).ToArray();
            }
            finally
            {
                if (outBlob.Data is not null)
                {
                    new Span<byte>(outBlob.Data, (int)outBlob.Size).Clear();
                    LocalFree((nint)outBlob.Data);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptProtectData(DataBlob* dataIn, string? description, DataBlob* entropy, nint reserved, nint prompt,
        int flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptUnprotectData(DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt,
        int flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
