using System.Globalization;
using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ImageFileDetail
{
    private const string ImageLayerOcr = "ocr";
    private const string ImageLayerGrid = "grid";
    private const string ImageLayerDebug = "debug";
    private const string ImageLayerSource = "source";

    [SupplyParameterFromQuery(Name = "path")]
    public string SourcePath { get; set; } = "";

    private ImageFileDetailResult? _detail;
    private bool _loading = true;
    private string? _error;
    private string _imageLayer = ImageLayerOcr;
    private bool _showOcrLabels = true;

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                _detail = new ImageFileDetailResult { Message = "画像ファイルが指定されていません。" };
                return;
            }

            _detail = await Timeline.GetImageFileDetailAsync(SourcePath);
            if (_detail.TextBlocks.All(block => !HasBbox(block)))
            {
                _imageLayer = _detail.Artifacts.HasDebugOverlay ? ImageLayerDebug : ImageLayerSource;
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetImageLayer(string layer)
    {
        if (layer == ImageLayerDebug && _detail?.Artifacts.HasDebugOverlay != true)
        {
            return;
        }

        _imageLayer = layer;
    }

    private void ToggleOcrLabels()
    {
        _showOcrLabels = !_showOcrLabels;
    }

    private static string DisplayName(ImageItemRow item) =>
        !string.IsNullOrWhiteSpace(item.SourceDisplayName) ? item.SourceDisplayName : EmptyText(item.RelativePath);

    private static string ImageSourceUrl(ImageItemRow item) =>
        $"/api/image/source?path={Uri.EscapeDataString(item.SourcePath)}";

    private static string ImageArtifactUrl(string path) =>
        $"/api/image/artifact?path={Uri.EscapeDataString(path)}";

    private string PreviewImageUrl(ImageItemRow item) =>
        _detail?.Artifacts.HasNormalizedImage == true
            ? ImageArtifactUrl(_detail.Artifacts.NormalizedImagePath)
            : ImageSourceUrl(item);

    private string DebugOverlayUrl(ImageItemRow item) =>
        _detail?.Artifacts.HasDebugOverlay == true
            ? ImageArtifactUrl(_detail.Artifacts.DebugOverlayPath)
            : PreviewImageUrl(item);

    private string VisibleImageUrl(ImageItemRow item) =>
        _imageLayer == ImageLayerDebug ? DebugOverlayUrl(item) : PreviewImageUrl(item);

    private static string ArtifactLabel(ImageItemRow item) =>
        item.HasTimeline && item.HasImageRecord ? "作成済み" : "未作成";

    private static string ArtifactIcon(ImageItemRow item) =>
        item.HasTimeline && item.HasImageRecord ? "circle-check" : "circle-minus";

    private static string ArtifactPillClass(ImageItemRow item) =>
        item.HasTimeline && item.HasImageRecord
            ? "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800"
            : "tfa-status-pill border-amber-200 bg-amber-50 text-amber-800";

    private static string DimensionLabel(ImageRecordSummary record) =>
        record.Width > 0 && record.Height > 0 ? $"{record.Width} x {record.Height}" : "-";

    private static string TextSummary(ImageRecordSummary record)
    {
        if (!record.HasText)
        {
            return "なし";
        }

        return record.OcrBlockCount > 0 ? $"{record.OcrBlockCount} ブロック" : "あり";
    }

    private static string QualityLabel(ImageRecordSummary record)
    {
        var brightness = EmptyText(LevelLabel(record.BrightnessLevel));
        var contrast = EmptyText(LevelLabel(record.ContrastLevel));
        return $"{brightness} / {contrast}";
    }

    private static string ImageKindLabel(string value) =>
        (value ?? "").ToLowerInvariant() switch
        {
            "photo_with_text" => "画像と文字",
            "photo" => "写真",
            "screenshot" => "スクリーンショット",
            "document_or_screenshot" => "文書またはスクリーンショット",
            "document" => "文書",
            "diagram" => "図表",
            _ => EmptyText(value),
        };

    private static string LevelLabel(string? value) =>
        (value ?? "").ToLowerInvariant() switch
        {
            "dark" => "暗い",
            "bright" => "明るい",
            "normal" => "標準",
            "low" => "低",
            "medium" => "中",
            "high" => "高",
            _ => value ?? "",
        };

    private static string BlockText(ImageTextBlock block) =>
        !string.IsNullOrWhiteSpace(block.NormalizedText) ? block.NormalizedText : EmptyText(block.Text);

    private static string ConfidenceLabel(ImageTextBlock block)
    {
        var level = EmptyText(LevelLabel(block.ConfidenceLevel));
        if (block.ConfidenceScore is double score)
        {
            return $"{level} {score:P0}";
        }

        return level;
    }

    private static string ConfidencePill(string level) =>
        (level ?? "").ToLowerInvariant() switch
        {
            "high" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "medium" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "low" => "tfa-status-pill border-amber-200 bg-amber-50 text-amber-900",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static bool HasBbox(ImageTextBlock block) => HasBbox(block.BboxNorm);

    private static bool HasBbox(ImageTextRegion region) => HasBbox(region.BboxNorm);

    private static bool HasBbox(ImageGridCell cell) => HasBbox(cell.BboxNorm);

    private static bool HasBbox(IReadOnlyList<double> bbox) =>
        bbox.Count >= 4
        && bbox.Take(4).All(value => !double.IsNaN(value) && !double.IsInfinity(value));

    private static string BboxStyle(IReadOnlyList<double> bbox)
    {
        if (!HasBbox(bbox))
        {
            return "display:none;";
        }

        var x1 = Clamp01(bbox[0]);
        var y1 = Clamp01(bbox[1]);
        var x2 = Clamp01(bbox[2]);
        var y2 = Clamp01(bbox[3]);
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var width = Math.Abs(x2 - x1);
        var height = Math.Abs(y2 - y1);
        return FormattableString.Invariant(
            $"left:{left * 100:0.####}%;top:{top * 100:0.####}%;width:{width * 100:0.####}%;height:{height * 100:0.####}%;");
    }

    private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));

    private static string OverlayBoxClass(ImageTextBlock block) =>
        (block.ConfidenceLevel ?? "").ToLowerInvariant() switch
        {
            "high" => "tfa-image-ocr-box tfa-image-ocr-box-high",
            "medium" => "tfa-image-ocr-box tfa-image-ocr-box-medium",
            "low" => "tfa-image-ocr-box tfa-image-ocr-box-low",
            _ => "tfa-image-ocr-box",
        };

    private static string LayerButtonClass(string currentLayer, string targetLayer, bool disabled = false)
    {
        var baseClass = "tfa-image-layer-button";
        if (disabled)
        {
            return baseClass + " tfa-image-layer-button-disabled";
        }

        return currentLayer == targetLayer
            ? baseClass + " tfa-image-layer-button-active"
            : baseClass;
    }

    private static bool HasVisualDescription(ImageVisualDescription visual) =>
        !string.IsNullOrWhiteSpace(visual.Caption)
        || !string.IsNullOrWhiteSpace(visual.SceneSummary)
        || visual.Observations.Any(item => !string.IsNullOrWhiteSpace(item));

    private static string PaletteStyle(ImageColorPaletteEntry entry)
    {
        var color = string.IsNullOrWhiteSpace(entry.Hex) ? "#e2e8f0" : entry.Hex;
        return $"background:{color};";
    }

    private static string ColorLabel(ImageColorPaletteEntry entry)
    {
        var ratio = entry.Ratio is double value
            ? FormattableString.Invariant($"{value * 100:0}%")
            : "-";
        return $"{EmptyText(entry.Hex)} / {ratio}";
    }

    private int TextRegionCountForCell(ImageGridCell cell)
    {
        if (!HasBbox(cell) || _detail is null)
        {
            return 0;
        }

        return _detail.Layout.TextRegions.Count(region => HasBbox(region) && BboxIntersects(cell.BboxNorm, region.BboxNorm));
    }

    private static bool BboxIntersects(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var ax1 = Math.Min(Clamp01(a[0]), Clamp01(a[2]));
        var ay1 = Math.Min(Clamp01(a[1]), Clamp01(a[3]));
        var ax2 = Math.Max(Clamp01(a[0]), Clamp01(a[2]));
        var ay2 = Math.Max(Clamp01(a[1]), Clamp01(a[3]));
        var bx1 = Math.Min(Clamp01(b[0]), Clamp01(b[2]));
        var by1 = Math.Min(Clamp01(b[1]), Clamp01(b[3]));
        var bx2 = Math.Max(Clamp01(b[0]), Clamp01(b[2]));
        var by2 = Math.Max(Clamp01(b[1]), Clamp01(b[3]));

        return ax1 < bx2 && ax2 > bx1 && ay1 < by2 && ay2 > by1;
    }

    private string GridCellClass(ImageGridCell cell)
    {
        var count = TextRegionCountForCell(cell);
        return count > 0
            ? "tfa-image-grid-cell tfa-image-grid-cell-has-text"
            : "tfa-image-grid-cell tfa-image-grid-cell-empty";
    }

    private static string GridCellLabel(ImageGridCell cell, int textCount) =>
        textCount > 0
            ? $"OCR {textCount} 件"
            : "OCR座標なし";

    private IEnumerable<ImageTextBlock> OcrBlocksWithBbox =>
        _detail?.TextBlocks.Where(HasBbox) ?? [];

    private IEnumerable<ImageGridCell> GridCellsWithBbox =>
        _detail?.Layout.Grid.Where(HasBbox) ?? [];

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
