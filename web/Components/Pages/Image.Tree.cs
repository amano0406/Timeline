using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Image
{
    private void ToggleAll(ChangeEventArgs args)
    {
        if (IsChecked(args))
        {
            foreach (var item in SelectableItems)
            {
                _selected.Add(item.ItemId);
            }
        }
        else
        {
            foreach (var item in SelectableItems)
            {
                _selected.Remove(item.ItemId);
            }
        }
    }

    private void ToggleTreeRow(ImageFileTreeRow row, bool selected)
    {
        foreach (var itemId in row.ItemIds)
        {
            if (selected)
            {
                _selected.Add(itemId);
            }
            else
            {
                _selected.Remove(itemId);
            }
        }
    }

    private void ToggleItem(ImageItemRow item, bool selected)
    {
        if (string.IsNullOrWhiteSpace(item.ItemId))
        {
            return;
        }
        if (selected)
        {
            _selected.Add(item.ItemId);
        }
        else
        {
            _selected.Remove(item.ItemId);
        }
    }

    private void ClearSelection() => _selected.Clear();

    private void RemoveMissingSelections()
    {
        var visible = Items.Select(item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selected.RemoveWhere(itemId => !visible.Contains(itemId));
    }

    private bool IsSelected(ImageItemRow item) =>
        !string.IsNullOrWhiteSpace(item.ItemId) && _selected.Contains(item.ItemId);

    private List<string> SelectedGeneratedItemIds() =>
        Items.Where(item => _selected.Contains(item.ItemId) && HasGeneratedItem(item))
            .Select(item => item.ItemId)
            .Where(itemId => !string.IsNullOrWhiteSpace(itemId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsChecked(ChangeEventArgs args) =>
        args.Value is bool value && value;

    private static List<ImageFileTreeRow> BuildTreeRows(IEnumerable<ImageItemRow> files)
    {
        var virtualRoot = new FileTreeDirectory("");
        foreach (var file in files.OrderBy(FileSortPath, StringComparer.OrdinalIgnoreCase))
        {
            var directory = virtualRoot;
            foreach (var part in SplitPathParts(FileDirectoryPath(file)))
            {
                directory = directory.GetOrAddDirectory(part);
            }
            directory.Files.Add(file);
        }

        var rows = new List<ImageFileTreeRow>();
        AddDirectoryRows(virtualRoot, 0, rows);
        return rows;
    }

    private static void AddDirectoryRows(FileTreeDirectory directory, int depth, List<ImageFileTreeRow> rows)
    {
        foreach (var child in directory.Directories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ImageFileTreeRow(child.Name, depth, null, CollectItemIds(child)));
            AddDirectoryRows(child, depth + 1, rows);
        }

        foreach (var file in directory.Files.OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ImageFileTreeRow(DisplayName(file), depth, file, ImageItemKey(file)));
        }
    }

    private static List<string> CollectItemIds(FileTreeDirectory directory)
    {
        var keys = new List<string>();
        foreach (var child in directory.Directories)
        {
            keys.AddRange(CollectItemIds(child));
        }
        keys.AddRange(directory.Files.SelectMany(ImageItemKey));
        return keys;
    }

    private static IReadOnlyList<string> ImageItemKey(ImageItemRow item) =>
        string.IsNullOrWhiteSpace(item.ItemId) ? [] : [item.ItemId];

    private bool IsTreeRowSelected(ImageFileTreeRow row) =>
        row.ItemIds.Count > 0 && row.ItemIds.All(itemId => _selected.Contains(itemId));

    private bool IsTreeRowPartiallySelected(ImageFileTreeRow row) =>
        row.File is null
        && row.ItemIds.Any(itemId => _selected.Contains(itemId))
        && !IsTreeRowSelected(row);

    private static string SelectionLabel(ImageFileTreeRow row) =>
        row.File is null ? $"{row.Name} 配下を選択" : $"{row.Name} を選択";

    private static string FileDirectoryPath(ImageItemRow item)
    {
        var sourcePath = (item.SourcePath ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var index = LastPathSeparatorIndex(sourcePath);
            return index > 0 ? sourcePath[..index] : "";
        }

        var relativePath = (item.RelativePath ?? "").Trim();
        var relativeIndex = LastPathSeparatorIndex(relativePath);
        return relativeIndex > 0 ? relativePath[..relativeIndex] : "";
    }

    private static string FileSortPath(ImageItemRow item) =>
        !string.IsNullOrWhiteSpace(item.SourcePath)
            ? item.SourcePath
            : item.RelativePath;

    private static IEnumerable<string> SplitPathParts(string path)
    {
        var normalized = (path ?? "").Trim().Trim('\\', '/');
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : normalized.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int LastPathSeparatorIndex(string? path) =>
        (path ?? "").LastIndexOfAny(['\\', '/']);

    private static string TreeIndentStyle(int depth) =>
        $"--depth:{Math.Max(0, depth)}";

    private static string TreeRowClass(ImageFileTreeRow row) =>
        row.File is null ? "tfa-file-tree-row tfa-file-tree-row-directory" : "tfa-file-tree-row tfa-file-tree-row-file";

    private static string DisplayName(ImageItemRow item) =>
        !string.IsNullOrWhiteSpace(item.SourceDisplayName) ? item.SourceDisplayName : EmptyText(item.RelativePath);

    private static string DetailHref(ImageItemRow item) =>
        $"image/file-detail?path={Uri.EscapeDataString(item.SourcePath)}";

    private static string DirectoryDisplay(ImageInputRoot root) =>
        !string.IsNullOrWhiteSpace(root.DisplayPath) ? root.DisplayPath : EmptyText(root.Path);

    private static string ArtifactLabel(ImageItemRow item) =>
        HasGeneratedItem(item) ? "作成済み" : "未作成";

    private static string ArtifactPillClass(ImageItemRow item) =>
        HasGeneratedItem(item)
            ? "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800"
            : "tfa-status-pill border-amber-200 bg-amber-50 text-amber-800";

    private static bool HasGeneratedItem(ImageItemRow item) =>
        item.HasTimeline && item.HasImageRecord;

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ShortDate(string? value)
    {
        if (DateTimeOffset.TryParse(value, out var date))
        {
            return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        return EmptyText(value);
    }

    private static string FormatBytes(long value)
    {
        if (value <= 0)
        {
            return "-";
        }
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)value;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1)
        {
            size /= 1024;
            index += 1;
        }
        return $"{size:0.#} {units[index]}";
    }
}
