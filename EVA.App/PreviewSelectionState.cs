namespace EVA.App;

public sealed class PreviewSelectionState<T> where T : class, IDisposable
{
    public T? Image { get; private set; }
    public string? Message { get; private set; }

    public void SetImage(T image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Clear();
        Image = image;
    }

    public void SetFailure(string message)
    {
        Clear();
        Message = message;
    }

    public void Clear()
    {
        Image?.Dispose();
        Image = null;
        Message = null;
    }
}