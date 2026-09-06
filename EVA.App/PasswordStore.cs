using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EVA.App;

public static class PasswordStore
{
    public const int CRED_TYPE_GENERIC = 1;
    public static string PasswordReference => "EVA-EncryptionPassword";

    public static bool HasStoredPassword()
    {
        // return TryReadCredential(out _);
        var credResult = TryReadCredential(out _);
        var legacyPath = GetPasswordFilePath();
        var legacyExists = File.Exists(legacyPath);
        Console.Error.WriteLine($"HasStoredPassword: TryReadCredential={credResult}");
        Console.Error.WriteLine($"HasStoredPassword: LegacyPath={legacyPath}");
        Console.Error.WriteLine($"HasStoredPassword: LegacyFileExists={legacyExists}");
        return credResult;
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
            if (TryWindowsUnprotect(encrypted, out var decrypted))
            {
                return Encoding.UTF8.GetString(decrypted);
            }

            return DecryptText(encrypted);
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
        var protectedBytes = TryWindowsProtect(bytes, out var windowsProtected)
            ? windowsProtected
            : EncryptText(bytes);

        File.WriteAllBytes(filePath, protectedBytes);
    }

    private static bool TryWindowsProtect(byte[] plainBytes, out byte[] protectedBytes)
    {
        protectedBytes = Array.Empty<byte>();

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var protectedDataType = Type.GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData");
        if (protectedDataType is null)
        {
            return false;
        }

        var dataProtectionScopeType = Type.GetType("System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData");
        if (dataProtectionScopeType is null)
        {
            return false;
        }

        var protectMethod = protectedDataType.GetMethod("Protect", new[] { typeof(byte[]), typeof(byte[]), dataProtectionScopeType });
        if (protectMethod is null)
        {
            return false;
        }

        try
        {
            var scopeValue = Enum.Parse(dataProtectionScopeType, "CurrentUser");
            protectedBytes = (byte[])protectMethod.Invoke(null, new object[] { plainBytes, null!, scopeValue })!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWindowsUnprotect(byte[] protectedBytes, out byte[] plainBytes)
    {
        plainBytes = Array.Empty<byte>();

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var protectedDataType = Type.GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData");
        if (protectedDataType is null)
        {
            return false;
        }

        var dataProtectionScopeType = Type.GetType("System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData");
        if (dataProtectionScopeType is null)
        {
            return false;
        }

        var unprotectMethod = protectedDataType.GetMethod("Unprotect", new[] { typeof(byte[]), typeof(byte[]), dataProtectionScopeType });
        if (unprotectMethod is null)
        {
            return false;
        }

        try
        {
            var scopeValue = Enum.Parse(dataProtectionScopeType, "CurrentUser");
            plainBytes = (byte[])unprotectMethod.Invoke(null, new object[] { protectedBytes, null!, scopeValue })!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] EncryptText(byte[] plainBytes)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(GetAesKeySeed()));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        return payload;
    }

    private static string DecryptText(byte[] protectedBytes)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(GetAesKeySeed()));
        var nonce = new byte[12];
        var ciphertextLength = protectedBytes.Length - nonce.Length - 16;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[16];

        Buffer.BlockCopy(protectedBytes, 0, nonce, 0, nonce.Length);
        Buffer.BlockCopy(protectedBytes, nonce.Length, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(protectedBytes, nonce.Length + ciphertext.Length, tag, 0, tag.Length);

        using var aes = new AesGcm(key, 16);
        var decrypted = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, decrypted);
        return Encoding.UTF8.GetString(decrypted);
    }

    private static string GetAesKeySeed()
    {
        var user = Environment.UserName ?? "eva-user";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return $"EVA.PasswordStore:{user}:{appData}";
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
        bool result = CredWrite(ref nativeCredential, 0);
        int error = Marshal.GetLastWin32Error();
        System.Diagnostics.Debug.WriteLine($"CredWrite result: {result}, error: {error}");

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
