using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class AudioFiles
{
    private sealed class FileTreeDirectory
    {
        private readonly Dictionary<string, FileTreeDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);

        public FileTreeDirectory(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public IReadOnlyCollection<FileTreeDirectory> Directories => _directories.Values;
        public List<AudioFileRow> Files { get; } = [];

        public FileTreeDirectory GetOrAddDirectory(string name)
        {
            if (!_directories.TryGetValue(name, out var directory))
            {
                directory = new FileTreeDirectory(name);
                _directories.Add(name, directory);
            }

            return directory;
        }
    }

    private sealed record FileTreeRow(string Name, int Depth, AudioFileRow? File, IReadOnlyList<string> FileKeys);

    private static List<FileTreeRow> BuildTreeRows(IEnumerable<AudioFileRow> files)
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

        var rows = new List<FileTreeRow>();
        AddDirectoryRows(virtualRoot, 0, rows);
        return rows;
    }

    private static void AddDirectoryRows(FileTreeDirectory directory, int depth, List<FileTreeRow> rows)
    {
        foreach (var child in directory.Directories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new FileTreeRow(child.Name, depth, null, CollectFileKeys(child)));
            AddDirectoryRows(child, depth + 1, rows);
        }

        foreach (var file in directory.Files.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new FileTreeRow(file.FileName, depth, file, [FileKey(file)]));
        }
    }

    private static List<string> CollectFileKeys(FileTreeDirectory directory)
    {
        var keys = new List<string>();
        foreach (var child in directory.Directories)
        {
            keys.AddRange(CollectFileKeys(child));
        }
        keys.AddRange(directory.Files.Select(FileKey));
        return keys;
    }

    private static string FileDirectory(AudioFileRow file)
    {
        if (!string.IsNullOrWhiteSpace(file.Directory))
        {
            return file.Directory;
        }

        var relativePath = file.RelativePath ?? "";
        var index = LastPathSeparatorIndex(relativePath);
        return index > 0 ? relativePath[..index] : "";
    }

    private static string FileDirectoryPath(AudioFileRow file)
    {
        if (!string.IsNullOrWhiteSpace(file.DisplayPath))
        {
            var index = LastPathSeparatorIndex(file.DisplayPath);
            return index > 0 ? file.DisplayPath[..index] : "";
        }

        var root = (file.RootPath ?? "").Trim().TrimEnd('\\', '/');
        var directory = FileDirectory(file).Trim().Trim('\\', '/');
        if (string.IsNullOrWhiteSpace(directory))
        {
            return root;
        }

        return string.IsNullOrWhiteSpace(root) ? directory : $"{root}/{directory}";
    }

    private static string FileSortPath(AudioFileRow file)
    {
        if (!string.IsNullOrWhiteSpace(file.DisplayPath))
        {
            return file.DisplayPath;
        }

        return $"{file.RootPath}/{file.RelativePath}";
    }

    private static IEnumerable<string> SplitPathParts(string path)
    {
        var normalized = (path ?? "").Trim().Trim('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return normalized.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int LastPathSeparatorIndex(string? path) =>
        (path ?? "").LastIndexOfAny(['\\', '/']);

    private static string TreeIndentStyle(int depth) =>
        $"--depth:{Math.Max(0, depth)}";

    private static string TreeRowClass(FileTreeRow row) =>
        row.File is null ? "tfa-file-tree-row tfa-file-tree-row-directory" : "tfa-file-tree-row tfa-file-tree-row-file";

    private static string FileKey(AudioFileRow file) =>
        $"{file.SourceId}|{file.RelativePath}";

    private bool IsTreeRowSelected(FileTreeRow row) =>
        row.FileKeys.Count > 0 && row.FileKeys.All(key => _selectedFileKeys.Contains(key));

    private bool IsTreeRowPartiallySelected(FileTreeRow row) =>
        row.File is null
        && row.FileKeys.Any(key => _selectedFileKeys.Contains(key))
        && !IsTreeRowSelected(row);

    private static string SelectionLabel(FileTreeRow row) =>
        row.File is null ? $"{row.Name} 配下を選択" : $"{row.Name} を選択";

    private static bool IsChecked(ChangeEventArgs args) =>
        args.Value is bool value && value;

    private void ToggleAllVisible(ChangeEventArgs args)
    {
        _operationMessage = null;
        if (IsChecked(args))
        {
            foreach (var file in Files)
            {
                _selectedFileKeys.Add(FileKey(file));
            }
        }
        else
        {
            foreach (var file in Files)
            {
                _selectedFileKeys.Remove(FileKey(file));
            }
        }
    }

    private void ToggleTreeRow(FileTreeRow row, bool selected)
    {
        _operationMessage = null;
        foreach (var key in row.FileKeys)
        {
            if (selected)
            {
                _selectedFileKeys.Add(key);
            }
            else
            {
                _selectedFileKeys.Remove(key);
            }
        }
    }

    private void ClearSelection()
    {
        _operationMessage = null;
        _selectedFileKeys.Clear();
    }

    private IEnumerable<AudioFileRow> SelectedFiles() =>
        Files.Where(file => _selectedFileKeys.Contains(FileKey(file)));

    private IReadOnlyList<string> SelectedGeneratedItemIds() =>
        SelectedGeneratedItemIds(SelectedFiles()).ToList();

    private static IEnumerable<string> SelectedGeneratedItemIds(IEnumerable<AudioFileRow> files) =>
        files.Select(GeneratedItemId)
            .Where(itemId => !string.IsNullOrWhiteSpace(itemId))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private void RemoveMissingSelections()
    {
        var visibleKeys = Files.Select(FileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedFileKeys.RemoveWhere(key => !visibleKeys.Contains(key));
    }

    private static string GeneratedItemId(AudioFileRow file)
    {
        if (!string.IsNullOrWhiteSpace(file.ItemId) && !file.ItemId.Contains(':'))
        {
            return file.ItemId;
        }

        return "";
    }

    private static bool HasGeneratedItem(AudioFileRow file) =>
        !string.IsNullOrWhiteSpace(GeneratedItemId(file));

    private static bool IsProcessingStatus(string status) =>
        status.Equals("processing", StringComparison.OrdinalIgnoreCase)
        || status.Equals("queued", StringComparison.OrdinalIgnoreCase);

    private static string SourceFileIdentity(AudioFileRow file)
    {
        if (!string.IsNullOrWhiteSpace(file.SourceFileIdentity))
        {
            return file.SourceFileIdentity;
        }

        return $"{file.SourceId}:{(file.RelativePath ?? "").Replace('\\', '/')}";
    }

    private static string DeleteGeneratedMessage(AudioDeleteGeneratedResult result, int selectedCount)
    {
        if (result.CatalogRowsRemoved > 0)
        {
            var message = $"{result.CatalogRowsRemoved} 件の生成物を削除しました。";
            if (result.MissingSourceFileIdentities.Count > 0)
            {
                message += $" 生成物がないファイル: {result.MissingSourceFileIdentities.Count} 件。";
            }
            return message;
        }

        return $"選択した {selectedCount} 件に削除対象の生成物はありませんでした。";
    }
}
