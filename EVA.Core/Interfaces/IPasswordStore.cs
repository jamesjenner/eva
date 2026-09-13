namespace EVA.Core.Interfaces;

public interface IPasswordStore
{
    bool HasStoredPassword();
    string? GetPassword();
    void SavePassword(string password);
    void RemovePassword();
    string PasswordReference { get; }
}