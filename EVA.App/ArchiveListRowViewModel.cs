using System.ComponentModel;
using System.Runtime.CompilerServices;
using EVA.Core;
using EVA.Core.Models;

namespace EVA.App;

public sealed record ArchiveLocationViewModel(string DisplayName, string Directory);

public sealed class ArchiveListRowViewModel : INotifyPropertyChanged
{
    private readonly string _fileName;
    private readonly ArchiveType _archiveType;
    private string _typeLabel;
    private string _displayText;
    private bool _isOther;

    private ArchiveListRowViewModel(
        string path,
        string heading,
        string fileName,
        ArchiveType archiveType,
        DateTimeOffset createdUtc,
        bool unreadableHeader = false)
    {
        Path = path;
        Heading = heading;
        _fileName = fileName;
        _archiveType = archiveType;
        TimestampLabel = path.Length == 0 || unreadableHeader
            ? string.Empty
            : createdUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm");
        _displayText = unreadableHeader ? fileName : TimestampLabel;
        _typeLabel = path.Length == 0
            ? string.Empty
            : unreadableHeader
                ? ArchiveContentProjection.GetArchiveTypeLabelFromFilename(fileName)
                : ArchiveContentProjection.GetArchiveTypeLabel(fileName, archiveType);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Path { get; }
    public string Heading { get; }
    public string TimestampLabel { get; }
    public string DisplayText => _displayText;
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

    public static ArchiveListRowViewModel ForOtherArchive(string path, ArchiveHeader header)
    {
        var row = ForArchive(path, header);
        row.SetOtherDisplay();
        return row;
    }

    public static ArchiveListRowViewModel ForUnreadableArchive(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileName = System.IO.Path.GetFileName(path);
        return new ArchiveListRowViewModel(path, string.Empty, fileName, default, default, unreadableHeader: true);
    }

    public static IReadOnlyList<ArchiveListRowViewModel> BuildRows(
        IReadOnlyList<ArchiveDateGroup> groups,
        IReadOnlyDictionary<string, ArchiveListRowViewModel> archiveRowsByPath)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(archiveRowsByPath);

        var groupedPaths = groups.SelectMany(group => group.ArchivePaths).ToArray();
        if (groupedPaths.Length != archiveRowsByPath.Count ||
            groupedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupedPaths.Length ||
            groupedPaths.Any(path => !archiveRowsByPath.ContainsKey(path)))
        {
            throw new InvalidOperationException("Every archive must appear in exactly one display group.");
        }

        var rows = new List<ArchiveListRowViewModel>(groupedPaths.Length + groups.Count);
        foreach (var group in groups)
        {
            rows.Add(ForHeader(group.Heading));
            foreach (var path in group.ArchivePaths)
            {
                var archiveRow = archiveRowsByPath[path];
                if (group.Heading == "Other")
                {
                    archiveRow.SetOtherDisplay();
                }

                rows.Add(archiveRow);
            }
        }

        return rows;
    }

    public void SetManual(bool isManual)
    {
        if (!IsArchive || _isOther)
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

    private void SetOtherDisplay()
    {
        _isOther = true;
        _displayText = ArchiveDateGrouping.HasParseableTimestamp(Path) && TimestampLabel.Length > 0
            ? TimestampLabel
            : _fileName;
        _typeLabel = ArchiveContentProjection.GetArchiveTypeLabelFromFilename(_fileName);
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(IsFull));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}