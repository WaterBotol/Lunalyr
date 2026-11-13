using System.Runtime.InteropServices;

namespace Lunalyr.Install;

/// <summary>
/// Whether Windows trusts a file's Authenticode signature (<c>WinVerifyTrust</c>, the check SmartScreen and Explorer
/// build on). Used for downloaded updates once Lunalyr is signed. Revocation isn't checked online here, so the check
/// never reaches the network; reads no certificate store of its own.
/// </summary>
public static partial class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>0 when the signature is trusted; otherwise Windows' reason (for the log).</summary>
    public static unsafe int Check(string path)
    {
        fixed (char* file = Path.GetFullPath(path))
        {
            var fileInfo = new FileInfo { Size = (uint)sizeof(FileInfo), FilePath = file };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData),
                UiChoice = 2,            // WTD_UI_NONE
                RevocationChecks = 0,    // WTD_REVOKE_NONE
                UnionChoice = 1,         // WTD_CHOICE_FILE
                File = &fileInfo,
                StateAction = 1,         // WTD_STATEACTION_VERIFY
                ProvFlags = 0x1000,      // WTD_CACHE_ONLY_URL_RETRIEVAL: never fetch anything
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(-1 /* INVALID_HANDLE_VALUE: no UI */, &action, &data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(-1, &action, &data);
            return result;
        }
    }

    public static bool IsTrusted(string path) => File.Exists(path) && Check(path) == 0;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileInfo
    {
        public uint Size;
        public char* FilePath;
        public nint File;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public FileInfo* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint hwnd, Guid* action, TrustData* data);
}
