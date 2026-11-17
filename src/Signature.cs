using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace RamOptimiser
{
    /// <summary>
    /// What the file's embedded Authenticode signature says. NotChecked: missing, not on a local drive, a cloud
    /// placeholder or link, or the check failed. Display only: no state unlocks or enables anything.
    /// </summary>
    public enum SignatureState { NotChecked, Signed, Unsigned, Invalid, Distrusted }

    /// <summary>One file's signature check. Immutable, so the session cache can hand the same object out.</summary>
    public sealed class SignatureInfo
    {
        public readonly SignatureState State;
        /// <summary>The signing certificate's simple display name; only for Signed, and null if unreadable.</summary>
        public readonly string Signer;
        /// <summary>WinVerifyTrust's raw LONG (0 when not checked), kept for diagnostics.</summary>
        public readonly int Status;

        public SignatureInfo(SignatureState state, string signer) : this(state, signer, 0) { }

        public SignatureInfo(SignatureState state, string signer, int status)
        {
            State = state; Signer = signer; Status = status;
        }
    }

    /// <summary>
    /// Checks a startup program's embedded Authenticode signature with WinVerifyTrust and reads the signer's name from
    /// the same verified state, so there is no gap between checking and naming. No network: revocation is off and
    /// URL retrieval is cache-only (learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data),
    /// and the file must be on a local drive. Catalog-signed Windows files have no embedded signature and read as
    /// Unsigned here; the wording says "signature not checked" for those (phase 2 adds catalog checks).
    /// </summary>
    public static class Signature
    {
        // wintrust.h / winerror.h. WinVerifyTrust returns a LONG: compared as a plain int, never through HRESULT macros.
        const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
        const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
        const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
        const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
        const int CRYPT_E_SECURITY_SETTINGS = unchecked((int)0x80092026);

        /// <summary>
        /// The doc's own example (learn.microsoft.com/en-us/windows/win32/seccrypto/example-c-program--verifying-the-signature-of-a-pe-file):
        /// 0 is signed; TRUST_E_NOSIGNATURE is "not signed" only when GetLastError names one of three reasons, else
        /// the signature is broken or the file could not be read; explicit distrust and CRYPT_E_SECURITY_SETTINGS
        /// (policy, "no signature, publisher or timestamp errors") are Windows blocking it; anything else (digest,
        /// chain, subject not trusted) is a signature that does not check out. Pure.
        /// </summary>
        public static SignatureState Classify(int status, int lastError)
        {
            if (status == 0) return SignatureState.Signed;
            if (status == TRUST_E_NOSIGNATURE)
                return lastError == TRUST_E_NOSIGNATURE || lastError == TRUST_E_SUBJECT_FORM_UNKNOWN || lastError == TRUST_E_PROVIDER_UNKNOWN
                    ? SignatureState.Unsigned : SignatureState.Invalid;
            if (status == TRUST_E_EXPLICIT_DISTRUST || status == CRYPT_E_SECURITY_SETTINGS) return SignatureState.Distrusted;
            return SignatureState.Invalid;
        }

        static readonly HashSet<string> SuffixWords = new HashSet<string>(StringComparer.Ordinal) {
            "corporation", "corp", "inc", "incorporated", "ltd", "limited", "llc", "gmbh", "co", "company", "sa", "ag", "bv", "plc" };

        /// <summary>
        /// Whether two company names are the same company: case, punctuation and legal suffix words (Inc, Ltd, LLC...)
        /// are ignored, and one name may be the other plus more words ("Microsoft" and "Microsoft Windows"). An empty
        /// name, or one made only of suffix words, is never the same as anything. Pure.
        /// </summary>
        public static bool SameCompany(string a, string b)
        {
            string x = CompanyKey(a), y = CompanyKey(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;
            // Whole words only: "Micro" is not "Microsoft".
            return x.StartsWith(y + " ", StringComparison.Ordinal) || y.StartsWith(x + " ", StringComparison.Ordinal);
        }

        static string CompanyKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            var words = new List<string>();
            foreach (string w in sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!SuffixWords.Contains(w)) words.Add(w);
            return string.Join(" ", words.ToArray());
        }

        // FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (winnt.h): not in .NET 4.8's enum.
        const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

        /// <summary>
        /// Files the check leaves alone: a link (it could lead to a share) and an offline or cloud placeholder (reading
        /// it would download it, e.g. OneDrive Files On-Demand). Pure.
        /// </summary>
        public static bool SkipForAttributes(FileAttributes attributes)
        {
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
        }

        const int MaxCached = 512;
        static readonly object cacheGate = new object();
        static readonly Dictionary<string, SignatureInfo> cache = new Dictionary<string, SignatureInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly SignatureInfo NotChecked = new SignatureInfo(SignatureState.NotChecked, null, 0);

        /// <summary>
        /// Checks one file's embedded signature. Only a fully qualified path on a lettered local drive is opened (no
        /// share, device or relative path) and never a link or placeholder; anything else, a missing file or any failure
        /// is NotChecked. Results are cached for the session by full path, length and last write time, because hashing
        /// a big exe can take a few hundred milliseconds. Never throws. Runs on the caller's (worker) thread.
        /// </summary>
        public static SignatureInfo Inspect(string path)
        {
            if (!StartupApps.IsLocalDrivePath(path)) return NotChecked;
            try
            {
                string full = Path.GetFullPath(path);
                if (!StartupApps.IsLocalDrivePath(full)) return NotChecked;
                var info = new FileInfo(full);
                if (!info.Exists || SkipForAttributes(info.Attributes)) return NotChecked;
                string key = full + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                lock (cacheGate)
                {
                    SignatureInfo hit;
                    if (cache.TryGetValue(key, out hit)) return hit;
                }
                SignatureInfo result = Verify(full);
                lock (cacheGate)
                {
                    if (cache.Count >= MaxCached) cache.Clear();
                    cache[key] = result;
                }
                return result;
            }
            catch (ArgumentException) { return NotChecked; }
            catch (NotSupportedException) { return NotChecked; }
            catch (IOException) { return NotChecked; }
            catch (UnauthorizedAccessException) { return NotChecked; }
            catch (System.Security.SecurityException) { return NotChecked; }
            catch (Win32Exception) { return NotChecked; }
            catch (EntryPointNotFoundException) { return NotChecked; }
            catch (DllNotFoundException) { return NotChecked; }
        }

        // WINTRUST_ACTION_GENERIC_VERIFY_V2 (softpub.h).
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
        const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
        const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;
        const int MaxNameChars = 1024;

        static SignatureInfo Verify(string full)
        {
            // Hold the file open (others may still read it) so it can't be swapped between the check and the name;
            // WinVerifyTrust reads through this handle.
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                IntPtr filePath = Marshal.StringToHGlobalUni(full);
                IntPtr fileInfo = IntPtr.Zero;
                var data = new WINTRUST_DATA();
                Guid action = GenericVerifyV2;
                bool opened = false;
                try
                {
                    var file = new WINTRUST_FILE_INFO();
                    file.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
                    file.pcwszFilePath = filePath;
                    file.hFile = stream.SafeFileHandle.DangerousGetHandle();
                    fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                    Marshal.StructureToPtr(file, fileInfo, false);

                    data.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
                    data.dwUIChoice = WTD_UI_NONE;
                    data.fdwRevocationChecks = WTD_REVOKE_NONE;
                    data.dwUnionChoice = WTD_CHOICE_FILE;
                    data.pFile = fileInfo;
                    data.dwStateAction = WTD_STATEACTION_VERIFY;
                    data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE;

                    int status = WinVerifyTrust(NoInteractiveUser, ref action, ref data);
                    int lastError = Marshal.GetLastWin32Error();
                    opened = true;
                    SignatureState state = Classify(status, lastError);
                    string signer = state == SignatureState.Signed ? SignerName(data.hWVTStateData) : null;
                    return new SignatureInfo(state, signer, status);
                }
                finally
                {
                    // The doc: every WTD_STATEACTION_VERIFY needs a WTD_STATEACTION_CLOSE, whatever the result.
                    if (opened)
                    {
                        data.dwStateAction = WTD_STATEACTION_CLOSE;
                        WinVerifyTrust(NoInteractiveUser, ref action, ref data);
                    }
                    if (fileInfo != IntPtr.Zero) Marshal.FreeHGlobal(fileInfo);
                    Marshal.FreeHGlobal(filePath);
                }
            }
        }

        /// <summary>INVALID_HANDLE_VALUE: "there is no interactive user" (WinVerifyTrust's hwnd), so it never shows UI.</summary>
        static readonly IntPtr NoInteractiveUser = new IntPtr(-1);

        /// <summary>
        /// The leaf certificate's simple display name from the verified state: CRYPT_PROVIDER_DATA, then the first
        /// signer (CRYPT_PROVIDER_SGNR), then pasCertChain[0].pCert. Offsets come from declared mirrors of the
        /// documented structs. Null when any step is missing.
        /// </summary>
        static string SignerName(IntPtr stateData)
        {
            if (stateData == IntPtr.Zero) return null;
            IntPtr prov = WTHelperProvDataFromStateData(stateData);
            if (prov == IntPtr.Zero) return null;
            IntPtr sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            if (sgnr == IntPtr.Zero) return null;
            int count = Marshal.ReadInt32(sgnr, (int)Marshal.OffsetOf(typeof(CRYPT_PROVIDER_SGNR), "csCertChain"));
            IntPtr chain = Marshal.ReadIntPtr(sgnr, (int)Marshal.OffsetOf(typeof(CRYPT_PROVIDER_SGNR), "pasCertChain"));
            if (count < 1 || chain == IntPtr.Zero) return null;
            IntPtr cert = Marshal.ReadIntPtr(chain, (int)Marshal.OffsetOf(typeof(CRYPT_PROVIDER_CERT), "pCert"));
            if (cert == IntPtr.Zero) return null;

            // Two calls: the first returns the length in characters, with the final null.
            uint needed = CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, null, 0);
            if (needed <= 1 || needed > MaxNameChars) return null;
            var sb = new StringBuilder((int)needed);
            uint got = CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, sb, needed);
            if (got <= 1) return null;
            string name = sb.ToString().Trim();
            return name.Length == 0 ? null : name;
        }

        // Mirrors of documented structs: some fields are only ever read through Marshal.OffsetOf or set by Windows.
#pragma warning disable 0649
        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
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

        /// <summary>learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-crypt_provider_sgnr</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct CRYPT_PROVIDER_SGNR
        {
            public uint cbStruct;
            public ComFileTime sftVerifyAsOf;
            public uint csCertChain;
            public IntPtr pasCertChain;
            public uint dwSignerType;
            public IntPtr psSigner;
            public uint dwError;
            public uint csCounterSigners;
            public IntPtr pasCounterSigners;
            public IntPtr pChainContext;
        }

        /// <summary>learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-crypt_provider_cert (the leading fields).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct CRYPT_PROVIDER_CERT
        {
            public uint cbStruct;
            public IntPtr pCert;
        }
#pragma warning restore 0649

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        // No import library, per the WTHelper docs; DllImport finds them by name anyway.
        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, bool fCounterSigner, uint idxCounterSigner);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
        static extern uint CertGetNameStringW(IntPtr pCertContext, uint dwType, uint dwFlags, IntPtr pvTypePara,
            StringBuilder pszNameString, uint cchNameString);
    }
}
