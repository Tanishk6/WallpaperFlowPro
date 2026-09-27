using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WallpaperFlow.Core.Native;

/// <summary>Authenticode verification (WinVerifyTrust) and publisher extraction for update installers.</summary>
internal static class Authenticode
{
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct; public string pcwszFilePath; public nint hFile; public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public nint pPolicyCallbackData; public nint pSIPClientData; public uint dwUIChoice;
        public uint fdwRevocationChecks; public uint dwUnionChoice; public nint pFile; public uint dwStateAction;
        public nint hWVTStateData; public nint pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private const uint WTD_UI_NONE = 2, WTD_REVOKE_WHOLECHAIN = 1, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1,
        WTD_STATEACTION_CLOSE = 2, WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;

    /// <summary>Returns true when the file carries a valid, trusted Authenticode signature.</summary>
    public static bool IsTrusted(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo));
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE, pFile = pFile, dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
            };
            var result = WinVerifyTrust(-1, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(-1, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    /// <summary>Signer subject CN, or null if unsigned.</summary>
    public static string? PublisherOf(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var c2 = new X509Certificate2(cert);
            return c2.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return null; }
    }
}

/// <summary>DPAPI (CurrentUser) wrapper used for API keys, so secrets never sit in settings.json in clear text.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public nint pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB input, string? desc, nint entropy, nint reserved, nint prompt, int flags, ref DATA_BLOB output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB input, nint desc, nint entropy, nint reserved, nint prompt, int flags, ref DATA_BLOB output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint mem);
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new DATA_BLOB();
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, "WallpaperFlow", 0, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != 0) LocalFree(output.pbData);
        }
    }
}
