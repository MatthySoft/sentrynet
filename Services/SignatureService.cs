using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SentryNet.Services;

/// <summary>
/// Verifies Authenticode signatures off the UI thread, with per-path caching.
/// A file counts as signed if it has a valid embedded signature (WinVerifyTrust)
/// or its hash appears in a registered security catalog — how most Windows system
/// binaries (svchost, lsass, …) are signed. Unreadable files fail open so I/O
/// errors don't raise false alarms.
/// </summary>
public sealed class SignatureService
{
    readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the trust verdict if already cached; otherwise starts one
    /// background check per path and reports through <paramref name="onResult"/>
    /// (on a worker thread) when it completes.</summary>
    public bool? IsTrusted(string exePath, Action<string, bool> onResult)
    {
        if (_cache.TryGetValue(exePath, out bool known)) return known;
        if (_pending.TryAdd(exePath, 0))
            Task.Run(() =>
            {
                bool trusted;
                try { trusted = HasValidEmbeddedSignature(exePath) || IsCatalogSigned(exePath); }
                catch { trusted = true; }
                _cache[exePath] = trusted;
                _pending.TryRemove(exePath, out _);
                onResult(exePath, trusted);
            });
        return null;
    }

    // ---- embedded Authenticode (WinVerifyTrust) ----

    static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WinTrustFileInfo
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustData
    {
        public int cbStruct;
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

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, in Guid actionId, ref WinTrustData data);

    static bool HasValidEmbeddedSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
        };
        IntPtr pFileInfo = Marshal.AllocHGlobal(fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);
            var data = new WinTrustData
            {
                cbStruct = Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE — no network stalls
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFileInfo,
                dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            return WinVerifyTrust(IntPtr.Zero, in ActionGenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFileInfo);
            Marshal.FreeHGlobal(pFileInfo);
        }
    }

    // ---- catalog signature (hash lookup in the system catalog database) ----

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptCATAdminAcquireContext2(out IntPtr hCatAdmin, IntPtr pgSubsystem,
        string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile,
        ref int pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll")]
    static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash,
        int cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll")]
    static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    static bool IsCatalogSigned(string path)
    {
        if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
            return false;
        try
        {
            // Share liberally — the exe is held open by its own running process.
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            int cb = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref cb, null, 0);
            if (cb <= 0) return false;
            var hash = new byte[cb];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref cb, hash, 0))
                return false;

            IntPtr prev = IntPtr.Zero;
            IntPtr cat = CryptCATAdminEnumCatalogFromHash(admin, hash, cb, 0, ref prev);
            if (cat == IntPtr.Zero) return false;
            CryptCATAdminReleaseCatalogContext(admin, cat, 0);
            return true;
        }
        finally
        {
            CryptCATAdminReleaseContext(admin, 0);
        }
    }
}
