using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ImageFileDetail
{
    [SupplyParameterFromQuery(Name = "path")]
    public string SourcePath { get; set; } = "";

    private ImageFileDetailResult? _detail;
    private bool _loading = true;
    private string? _error;

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

    private static string DisplayName(ImageItemRow item) =>
        !string.IsNullOrWhiteSpace(item.SourceDisplayName) ? item.SourceDisplayName : EmptyText(item.RelativePath);

    private static string ImageSourceUrl(ImageItemRow item) =>
        $"api/image/source?path={Uri.EscapeDataString(item.SourcePath)}";

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
            "low" => "低い",
            "medium" => "中",
            "high" => "高い",
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

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
