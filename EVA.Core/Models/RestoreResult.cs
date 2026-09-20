namespace EVA.Core.Models;

public sealed class RestoreResult
{
    public bool Success { get; set; }
    public List<string> FilesRestored { get; } = [];
    public List<string> FilesToOverwrite { get; } = [];
    public List<string> Errors { get; } = [];
}
