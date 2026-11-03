using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Lumen.Core.Diagnostics;

namespace Lumen.PowerHelper;

/// <summary>
/// The helper runs elevated, so it only starts PresentMon if the file carries a valid Authenticode
/// signature from Intel. A replaced or tampered exe is refused (FPS then reports "unavailable").
/// </summary>
internal static class SignatureCheck
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsSignedBy(string path, string organization, out string? problem)
    {
        problem = null;
        try
        {
            if (!VerifyTrust(path))
            {
                problem = "signature is missing or invalid";
                return false;
            }
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!cert.Subject.Contains($"O={organization}", StringComparison.OrdinalIgnoreCase))
            {
                problem = $"signed by '{cert.GetNameInfo(X509NameType.SimpleName, false)}', expected {organization}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Signature check failed for {path}.", ex);
            problem = "signature could not be read";
            return false;
        }
    }

    private static bool VerifyTrust(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (works offline)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

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

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
