using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace AuraClean.Helpers;

/// <summary>
/// Verifies embedded Authenticode signatures with WinVerifyTrust.
/// Used to avoid flagging properly signed software with heuristics and to refuse
/// running tampered or unsigned installers without an explicit user decision.
/// </summary>
public static class AuthenticodeHelper
{
    public enum SignatureStatus
    {
        Valid,
        Unsigned,
        Invalid,
        Unknown
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    private const uint WTD_DISABLE_MD2_MD4 = 0x00002000;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), SignatureStatus> Cache =
        new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    /// <summary>True when the file carries a valid, trusted embedded signature.</summary>
    public static bool IsSignedAndTrusted(string path) => Verify(path) == SignatureStatus.Valid;

    /// <summary>
    /// Verifies the embedded signature of <paramref name="path"/>. Results are cached per
    /// (path, size, last-write time). Revocation is not checked to keep scans offline and fast.
    /// </summary>
    public static SignatureStatus Verify(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return SignatureStatus.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SignatureStatus.Unknown;
        }

        var key = (info.FullName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc);
        return Cache.GetOrAdd(key, _ => VerifyCore(info.FullName));
    }

    private static SignatureStatus VerifyCore(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        bool fileStructWritten = false;

        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            fileStructWritten = true;

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_DISABLE_MD2_MD4
            };
            Marshal.StructureToPtr(data, pData, false);

            int result = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, pData);

            // Release the provider state allocated by the verify call.
            data = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, pData);

            return result switch
            {
                0 => SignatureStatus.Valid,
                TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureStatus.Unsigned,
                _ => SignatureStatus.Invalid
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            DiagnosticLogger.Warn("Authenticode", $"Signature check failed for {path}", ex);
            return SignatureStatus.Unknown;
        }
        finally
        {
            if (fileStructWritten)
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pData);
        }
    }
}
