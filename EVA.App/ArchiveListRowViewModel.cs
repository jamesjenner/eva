using System.ComponentModel;
using System.Runtime.CompilerServices;
using EVA.Core;
using EVA.Core.Models;

namespace EVA.App;

public sealed class ArchiveListRowViewModel : INotifyPropertyChanged
{
    private readonly string _fileName;
    private readonly ArchiveType _archiveType;
    private string _typeLabel;

    private ArchiveListRowViewModel(string path, string heading, string fileName, ArchiveType archiveType, DateTimeOffset createdUtc)
    {
        Path = path;
        Heading = heading;
        _fileName = fileName;
        _archiveType = archiveType;
        TimestampLabel = path.Length == 0
            ? string.Empty
            : createdUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm");
        _typeLabel = path.Length == 0
            ? string.Empty
            : ArchiveContentProjection.GetArchiveTypeLabel(fileName, archiveType);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Path { get; }
    public string Heading { get; }
    public string TimestampLabel { get; }
    public string TypeLabel => _typeLabel;
    public bool IsHeader => !IsArchive;
    public bool IsArchive => Path.Length > 0;
    public bool IsSelectable => IsArchive;
    public bool IsFull => _typeLabel.StartsWith("Full", StringComparison.Ordinal);

    public static ArchiveListRowViewModel ForHeader(string heading) =>
        new(string.Empty, heading, string.Empty, default, default);

    public static ArchiveListRowViewModel ForArchive(string path, ArchiveHeader header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(header);

        return new ArchiveListRowViewModel(
            path,
            string.Empty,
            System.IO.Path.GetFileName(path),
            header.ArchiveType,
            header.CreatedUtc);
    }

    public void SetManual(bool isManual)
    {
        if (!IsArchive)
        {
            return;
        }

        var updatedLabel = ArchiveContentProjection.GetArchiveTypeLabel(_fileName, _archiveType, isManual);
        if (_typeLabel == updatedLabel)
        {
            return;
        }

        _typeLabel = updatedLabel;
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(IsFull));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}