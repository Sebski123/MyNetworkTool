using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace NetworkingTool.Install;

/// <summary>
/// Verifies that a downloaded executable is Authenticode-signed by a specific, pinned certificate
/// before the LocalSystem service is allowed to install it over the running binary.
///
/// The model is deliberately self-signing friendly: identity is pinned by certificate thumbprint
/// (so the publisher is fixed to YOUR key, not a publicly-trusted CA), and the cryptographic
/// signature is validated with WinVerifyTrust purely to confirm the file's bytes still match the
/// signature (tamper detection). Because the pinned cert's root is intentionally not in the trusted
/// store, an "untrusted root" / "chaining" result from WinVerifyTrust is treated as success — the
/// identity is already guaranteed by the thumbprint match. A bad digest, missing signature, or
/// explicit distrust is rejected.
/// </summary>
internal static class CodeSigning
{
    /// <summary>
    /// Returns true only if <paramref name="filePath"/> is Authenticode-signed by the certificate
    /// whose SHA-1 thumbprint equals <paramref name="expectedThumbprint"/> AND the signature
    /// validates over the file's current contents. Fails closed: an empty pinned thumbprint, an
    /// unsigned file, a wrong signer, or a tampered file all return false with an explanatory
    /// <paramref name="error"/>.
    /// </summary>
    public static bool VerifyPinned(string filePath, string expectedThumbprint, out string error)
    {
        error = string.Empty;

        string pinned = NormalizeThumbprint(expectedThumbprint);
        if (pinned.Length == 0)
        {
            error = "no code-signing thumbprint is configured (Constants.ExpectedCodeSigningThumbprint " +
                    "is empty), so downloaded updates cannot be verified and are refused.";
            return false;
        }

        if (!File.Exists(filePath))
        {
            error = "the downloaded file does not exist.";
            return false;
        }

        // 1. Extract the Authenticode signer certificate from the file. CreateFromSignedFile is the
        //    standard API for reading the signer of a signed file; the newer X509CertificateLoader
        //    only loads raw certificate bytes, not signatures, so the obsoletion is suppressed here.
        string actualThumbprint;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            actualThumbprint = NormalizeThumbprint(cert.Thumbprint);
        }
        catch (Exception ex)
        {
            error = "the file is not Authenticode-signed (" + ex.Message + ").";
            return false;
        }

        // 2. Pin the signer identity to the expected certificate.
        if (!string.Equals(actualThumbprint, pinned, StringComparison.OrdinalIgnoreCase))
        {
            error = $"the file is signed by an unexpected certificate (thumbprint {actualThumbprint}).";
            return false;
        }

        // 3. Confirm the signature covers the file's current bytes (tamper detection). The pinned
        //    cert's private root is not publicly trusted, so accept untrusted-root / chaining here;
        //    reject everything else (notably a bad digest, which means the file was altered).
        int hr = WinVerifyTrustFile(filePath);
        switch (unchecked((uint)hr))
        {
            case 0:                     // S_OK — trusted
            case CERT_E_UNTRUSTEDROOT:  // valid signature, self-signed / private root (expected)
            case CERT_E_CHAINING:       // valid signature, chain does not reach a trusted root
                return true;
            default:
                error = $"the file's Authenticode signature did not validate (0x{hr:X8}).";
                return false;
        }
    }

    /// <summary>Keeps only hex digits and upper-cases them, so pasted thumbprints with spaces match.</summary>
    private static string NormalizeThumbprint(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    // ---- WinVerifyTrust P/Invoke ----

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint CERT_E_CHAINING = 0x800B010A;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
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
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    private static int WinVerifyTrustFile(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int hr = WinVerifyTrust(IntPtr.Zero, action, ref data);

            // Always release the state data the verify action allocated.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, action, ref data);

            return hr;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
