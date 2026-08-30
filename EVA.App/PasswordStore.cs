using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EVA.App;

public static class PasswordStore
{
    public static string PasswordReference => "local:eva-password";

    public static string? GetPassword(string? reference)
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

    public static void SavePassword(string password)
    {
        var filePath = GetPasswordFilePath();
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);

        var bytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(filePath, protectedBytes);
    }

    public static void RemovePassword()
    {
        var filePath = GetPasswordFilePath();
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string GetPasswordFilePath()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA");
        return Path.Combine(appData, "eva-password.bin");
    }
}
