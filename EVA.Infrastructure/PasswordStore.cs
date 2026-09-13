using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EVA.Core.Interfaces;

namespace EVA.Infrastructure;

public sealed class PasswordStore : IPasswordStore
{
    public const int CRED_TYPE_GENERIC = 1;
    private const string PasswordReferenceValue = "EVA-EncryptionPassword";
    public string PasswordReference => PasswordReferenceValue;

    public bool HasStoredPassword()
    {
        return TryReadCredential(out _);
    }

    public string? GetPassword()
    {
        return TryReadCredential(out var password) ? password : null;
    }

    public void SavePassword(string password)
    {
        if (!SaveCredential(password))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to save password to Windows Credential Manager (error code {error}). " +
                "Please ensure Windows Credential Manager is available and try again.");
        }
    }

    public void RemovePassword()
    {
        _ = TryDeleteCredential();
    }

    private bool SaveCredential(string password)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var nativeCredential = new NativeCredential
        {
            Flags = 0,
            Type = CRED_TYPE_GENERIC,
            TargetName = Marshal.StringToCoTaskMemUni(PasswordReference),
            Comment = IntPtr.Zero,
            LastWritten = 0,
            CredentialBlobSize = passwordBytes.Length,
            CredentialBlob = Marshal.AllocCoTaskMem(passwordBytes.Length),
            Persist = 1,
            AttributeCount = 0,
            Attributes = IntPtr.Zero,
            TargetAlias = IntPtr.Zero,
            UserName = Marshal.StringToCoTaskMemUni(Environment.UserName)
        };

        try
        {
            Marshal.Copy(passwordBytes, 0, nativeCredential.CredentialBlob, passwordBytes.Length);
            return CredWrite(ref nativeCredential, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(nativeCredential.TargetName);
            Marshal.FreeCoTaskMem(nativeCredential.CredentialBlob);
            Marshal.FreeCoTaskMem(nativeCredential.UserName);
        }
    }

    private bool TryReadCredential(out string? password)
    {
        password = null;
        if (!CredRead(PasswordReference, CRED_TYPE_GENERIC, 0, out var credentialPtr)) return false;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero) return false;
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            password = Encoding.Unicode.GetString(blob);
            return !string.IsNullOrEmpty(password);
        }
        finally { CredFree(credentialPtr); }
    }

    private bool TryDeleteCredential() => CredDelete(PasswordReference, CRED_TYPE_GENERIC, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags; public int Type; public IntPtr TargetName; public IntPtr Comment; public long LastWritten;
        public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist; public int AttributeCount;
        public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string targetName, int type, int reservedFlag, out IntPtr credentialPtr);
    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string targetName, int type, int reservedFlag);
    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);
}