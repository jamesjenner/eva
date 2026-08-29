namespace EVA.Core.Interfaces;

public interface IEventLogger
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message);
    void LogException(Exception exception, string message);
}
