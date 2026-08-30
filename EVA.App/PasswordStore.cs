using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EVA.App;

public static class PasswordStore
{
    public const int CRED_TYPE_GENERIC = 1;
    public static string PasswordReference => "local:eva-password";

    public static bool HasStoredPassword()
    {
        return TryReadCredential(out _);
    }

    public static string? GetPassword(string? reference)
    {
        if (TryReadCredential(out var password))
        {
            return password;
        }

        return GetLegacyPasswordFromFile();
    }

    public static void SavePassword(string password)
    {
        SaveCredential(password);
        SaveLegacyPasswordToFile(password);
    }

    public static void RemovePassword()
    {
        if (TryDeleteCredential())
        {
            // Credential manager entry removed.
        }

        var filePath = GetPasswordFilePath();
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string? GetLegacyPasswordFromFile()
    {
        var filePath = GetPasswordFilePath();
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(filePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLegacyPasswordToFile(string password)
    {
        var filePath = GetPasswordFilePath();
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);

        var bytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(filePath, protectedBytes);
    }

    private static void SaveCredential(string password)
    {
        var target = PasswordReference;
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var nativeCredential = new NativeCredential
        {
            Flags = 0,
            Type = CRED_TYPE_GENERIC,
            TargetName = Marshal.StringToCoTaskMemUni(target),
            Comment = IntPtr.Zero,
            LastWritten = DateTime.UtcNow.ToFileTimeUtc(),
            CredentialBlobSize = passwordBytes.Length,
            CredentialBlob = Marshal.AllocCoTaskMem(passwordBytes.Length),
            Persist = 1,
            AttributeCount = 0,
            Attributes = IntPtr.Zero,
            TargetAlias = IntPtr.Zero,
            UserName = Marshal.StringToCoTaskMemUni(Environment.UserName)
        };

        Marshal.Copy(passwordBytes, 0, nativeCredential.CredentialBlob, passwordBytes.Length);
        CredWrite(ref nativeCredential, 0);

        Marshal.FreeCoTaskMem(nativeCredential.TargetName);
        Marshal.FreeCoTaskMem(nativeCredential.CredentialBlob);
        Marshal.FreeCoTaskMem(nativeCredential.UserName);
    }

    private static bool TryReadCredential(out string? password)
    {
        password = null;
        if (!CredRead(PasswordReference, CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return false;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return false;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            password = Encoding.Unicode.GetString(blob);
            return !string.IsNullOrEmpty(password);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private static bool TryDeleteCredential()
    {
        return CredDelete(PasswordReference, CRED_TYPE_GENERIC, 0);
    }

    private static string GetPasswordFilePath()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA");
        return Path.Combine(appData, "eva-password.bin");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
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
