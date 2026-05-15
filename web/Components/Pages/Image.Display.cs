using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Image
{
    private IReadOnlyList<ImageItemRow> Items => _items;
    private bool Busy => _loading || _loadingMore || _downloading || _deleting;
    private bool HasSelection => _selected.Count > 0;
    private bool HasGeneratedSelection => SelectedGeneratedItemIds().Count > 0;
    private bool AllSelected => SelectableItems.Count > 0 && SelectableItems.All(item => _selected.Contains(item.ItemId));
    private IReadOnlyList<ImageItemRow> SelectableItems => Items.Where(item => !string.IsNullOrWhiteSpace(item.ItemId)).ToList();
    private int SourceFileTotal => _files?.Total > 0
        ? _files.Total
        : _pagination?.TotalItems > 0
            ? _pagination.TotalItems
            : _overview?.SourceFileCount ?? _items.Count;
    private int ProcessedFileTotal => _files is not null
        ? _files.ProcessedTotal
        : Math.Min(_overview?.ItemCount ?? 0, SourceFileTotal);
    private string ImportedFileCountLabel => $"{ProcessedFileTotal:N0} / {SourceFileTotal:N0}";
    private int UnprocessedCount => Math.Max(0, (_overview?.SourceFileCount ?? 0) - (_overview?.ItemCount ?? 0));
    private string ProcessingLabel => "待機中";
    private string ProcessingIcon => "circle-check";
    private string ProcessingIconClass => "text-teal-700";
    private string SelectionSummary => HasSelection ? $"{_selected.Count} 件選択中" : "未選択";
    private int ListTotalItems => _pagination?.TotalItems > 0
        ? _pagination.TotalItems
        : _overview?.SourceFileCount ?? _items.Count;
    private bool ShowInputDirectoryWarning =>
        _overview?.ProductFound == true &&
        _overview.SettingsValid &&
        _overview.SourceFileCount == 0;
    private string InputDirectoryWarningTitle =>
        MissingInputRoots.Count > 0
            ? "入力ディレクトリが見つかりません。"
            : "入力ディレクトリに画像ファイルがありません。";
    private string InputDirectoryWarningDetail =>
        string.Join(" / ", (_overview?.Settings.InputRoots ?? []).Select(DirectoryDisplay));
    private IReadOnlyList<ImageInputRoot> MissingInputRoots =>
        _overview?.Settings.InputRoots.Where(root => !root.Exists).ToList() ?? [];
}
