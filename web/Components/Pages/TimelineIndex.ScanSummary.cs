using System.Globalization;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class TimelineIndex
{
    private TimelineProductOverview? _audio;
    private WindowsCodexOverview? _windowsCodex;
    private ChatGptOverview? _chatGpt;
    private ImageOverview? _image;
    private VideoOverview? _video;
    private PcOverview? _pc;
    private bool _loadingScanDataSources;

    private IReadOnlyList<ScanDataSourceSummary> ScanDataSources
    {
        get
        {
            if (_runtime is null && _overview is null && _audio is null && _video is null && _image is null && _chatGpt is null && _windowsCodex is null && _pc is null)
            {
                return [];
            }

            var audioStoreCount = ScanStoreItemCount("audio", _audio?.AudioItemCount ?? 0);
            var videoStoreCount = ScanStoreItemCount("video", _video?.ItemCount ?? 0);
            var imageStoreCount = ScanStoreItemCount("image", _image?.ItemCount ?? 0);
            var chatGptStoreCount = ScanStoreItemCount("chatgpt", _chatGpt?.ItemCount ?? 0);
            var windowsStoreCount = ScanStoreItemCount("windows-codex", _windowsCodex?.Current.ThreadCount ?? 0);
            var pcStoreCount = ScanStoreItemCount("pc", _pc?.ItemCount ?? 0);

            return
            [
                new("audio", "音声ファイル", "file-audio", "音声の取り込みと言語化候補", ScanSourceState(_audio?.ProductFound == true || IsInstalledProduct("audio"), audioStoreCount), [
                    new("対象", ScanDetailText(_audio is null, FormatNumber(_audio?.AudioFileCount ?? 0))),
                    new("処理済み", ScanDetailText(_audio is null, FormatNumber(_audio?.AudioItemCount ?? 0))),
                    new("Timeline", FormatNumber(audioStoreCount)),
                    new("言語化", ScanAudioVerbalizationSummaryText),
                ]),
                new("video", "動画ファイル", "video", "動画の取り込みと言語化候補", ScanSourceState(_video?.ProductFound == true || IsInstalledProduct("video"), videoStoreCount), [
                    new("対象", ScanDetailText(_video is null, FormatNumber(_video?.SourceFileCount ?? 0))),
                    new("処理済み", ScanDetailText(_video is null, FormatNumber(_video?.ItemCount ?? 0))),
                    new("Timeline", FormatNumber(videoStoreCount)),
                    new("言語化", ScanVideoVerbalizationSummaryText),
                ]),
                new("image", "画像ファイル", "image", "画像の取り込みとOCR候補", ScanSourceState(_image?.ProductFound == true || IsInstalledProduct("image"), imageStoreCount), [
                    new("対象", ScanDetailText(_image is null, FormatNumber(_image?.SourceFileCount ?? 0))),
                    new("処理済み", ScanDetailText(_image is null, FormatNumber(_image?.ItemCount ?? 0))),
                    new("Timeline", FormatNumber(imageStoreCount)),
                    new("言語化", "対象外"),
                ]),
                new("chatgpt", "ChatGPT", "comments", "会話スレッドの取り込み", ScanSourceState(_chatGpt?.ProductFound == true || IsInstalledProduct("chatgpt"), chatGptStoreCount), [
                    new("入力候補", ScanDetailText(_chatGpt is null, FormatNumber(_chatGpt?.ProcessableInputCount ?? 0))),
                    new("処理済み", ScanDetailText(_chatGpt is null, FormatNumber(_chatGpt?.ItemCount ?? 0))),
                    new("Timeline", FormatNumber(chatGptStoreCount)),
                    new("イベント", FormatNumber(ScanStoreEventCount("chatgpt"))),
                ]),
                new("windows-codex", "Windows Codex", "terminal", "Codex スレッドの取り込み", ScanSourceState(_windowsCodex?.ProductFound == true || IsInstalledProduct("windows-codex"), windowsStoreCount), [
                    new("スレッド", ScanDetailText(_windowsCodex is null, FormatNumber(_windowsCodex?.Current.ThreadCount ?? 0))),
                    new("処理済み", ScanDetailText(_windowsCodex is null, FormatNumber(ScanWindowsCodexProcessedCount(_windowsCodex)))),
                    new("Timeline", FormatNumber(windowsStoreCount)),
                    new("イベント", FormatNumber(ScanStoreEventCount("windows-codex"))),
                ]),
                new("pc", "PC状態", "desktop", "PC状態ログの取り込み", ScanSourceState(_pc?.ProductFound == true || IsInstalledProduct("pc"), pcStoreCount), [
                    new("対象", ScanDetailText(_pc is null, FormatNumber(_pc?.ItemCount ?? 0))),
                    new("処理済み", ScanDetailText(_pc is null, FormatNumber(_pc?.ItemCount ?? 0))),
                    new("Timeline", FormatNumber(pcStoreCount)),
                    new("保存先", ScanDetailText(_pc is null, _pc?.Settings.OutputRootReady == true ? "利用可" : "未設定")),
                ]),
            ];
        }
    }

    private int ScanTotalImportedItems => _overview?.Available == true
        ? _overview.ItemCount
        : (_audio?.AudioItemCount ?? 0)
            + (_video?.ItemCount ?? 0)
            + (_image?.ItemCount ?? 0)
            + (_windowsCodex?.Current.ThreadCount ?? 0)
            + (_chatGpt?.ItemCount ?? 0)
            + (_pc?.ItemCount ?? 0);

    private bool ScanRuntimeStatusKnown => _runtime?.Products.Count > 0;

    private int ScanTotalProductCount => ScanRuntimeStatusKnown
        ? _runtime!.Products.Count
        : (_overview?.ProductCount ?? _overview?.Products.Count ?? 0);

    private int ScanAvailableProductCount => ScanRuntimeStatusKnown
        ? _runtime!.Products.Count(product =>
            product.ProductFound
            && (product.ComposeFound || product.Id.Equals("pc", StringComparison.OrdinalIgnoreCase)))
        : (_overview?.Products.Count(product => product.Included || product.ItemCount > 0 || product.EventCount > 0) ?? 0);

    private string ScanVerbalizationTargetDisplay
    {
        get
        {
            if (AudioVerbalizationActive && _audioVerbalizationStatus is not null)
            {
                var remaining = Math.Max(0, _audioVerbalizationStatus.TotalItems - _audioVerbalizationStatus.CompletedItems - _audioVerbalizationStatus.SkippedItems - _audioVerbalizationStatus.FailedItems);
                return FormatNumber(remaining);
            }

            if (_loadingAudioVerbalizationTargets && _audioVerbalizationTargetSummary is null)
            {
                return "状態を確認しています";
            }

            return FormatNumber(_audioVerbalizationTargetSummary?.TargetCount ?? 0);
        }
    }

    private string ScanVerbalizationStateLabel
    {
        get
        {
            if (AudioVerbalizationActive && _audioVerbalizationStatus is not null)
            {
                var finished = _audioVerbalizationStatus.CompletedItems + _audioVerbalizationStatus.SkippedItems + _audioVerbalizationStatus.FailedItems;
                return $"{FormatNumber(finished)} / {FormatNumber(_audioVerbalizationStatus.TotalItems)} 処理中";
            }

            if (_loadingAudioVerbalizationTargets && _audioVerbalizationTargetSummary is null)
            {
                return "確認中";
            }

            return (_audioVerbalizationTargetSummary?.TargetCount ?? 0) > 0
                ? "未処理あり"
                : "未処理なし";
        }
    }

    private string ScanAudioVerbalizationSummaryText =>
        ScanDetailText(_audio is null, $"{FormatNumber(_audio?.AudioVerbalizedFileCount ?? 0)} / {FormatNumber(_audio?.AudioVerbalizationTargetFileCount ?? 0)}");

    private string ScanVideoVerbalizationSummaryText =>
        ScanDetailText(_video is null, $"{FormatNumber(_video?.AudioVerbalizedFileCount ?? 0)} / {FormatNumber(_video?.AudioVerbalizationTargetFileCount ?? 0)}");

    private async Task LoadScanDataSourcesAsync()
    {
        _loadingScanDataSources = true;
        try
        {
            if (ScanRuntimeStatusKnown && ScanAvailableProductCount == 0)
            {
                _audio = null;
                _windowsCodex = null;
                _chatGpt = null;
                _image = null;
                _video = null;
                _pc = null;
                return;
            }

            var audioTask = Timeline.GetAudioOverviewAsync();
            var windowsTask = Timeline.GetWindowsCodexOverviewAsync();
            var chatGptTask = Timeline.GetChatGptOverviewAsync();
            var imageTask = Timeline.GetImageOverviewAsync();
            var videoTask = Timeline.GetVideoOverviewAsync();
            var pcTask = Timeline.GetPcOverviewAsync();

            await Task.WhenAll(audioTask, windowsTask, chatGptTask, imageTask, videoTask, pcTask);

            _audio = await audioTask;
            _windowsCodex = await windowsTask;
            _chatGpt = await chatGptTask;
            _image = await imageTask;
            _video = await videoTask;
            _pc = await pcTask;
        }
        finally
        {
            _loadingScanDataSources = false;
        }
    }

    private string ScanDetailText(bool detailMissing, string value) =>
        _loadingScanDataSources && detailMissing ? "確認中" : value;

    private int ScanStoreItemCount(string productId, int fallback)
    {
        if (_overview?.Available == true)
        {
            return _overview.Products.FirstOrDefault(product => product.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase))?.ItemCount ?? 0;
        }

        return fallback;
    }

    private int ScanStoreEventCount(string productId) =>
        _overview?.Products.FirstOrDefault(product => product.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase))?.EventCount ?? 0;

    private static string ScanSourceState(bool productFound, int itemCount)
    {
        if (!productFound)
        {
            return "missing";
        }

        return itemCount > 0 ? "ready" : "empty";
    }

    private static string ScanSourceStateClass(ScanDataSourceSummary source) => source.State switch
    {
        "missing" => "border-red-200 bg-red-50 text-red-800",
        "empty" => "border-amber-200 bg-amber-50 text-amber-800",
        _ => "border-teal-200 bg-teal-50 text-teal-800",
    };

    private static int ScanWindowsCodexProcessedCount(WindowsCodexOverview? overview)
    {
        if (overview is null)
        {
            return 0;
        }

        return overview.Current.RenderedThreadCount > 0
            ? overview.Current.RenderedThreadCount
            : overview.Current.ThreadCount;
    }

    private static string FormatNumber(int value) => value.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"));

    private static string FormatNumber(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"));

    private sealed record ScanDataSourceSummary(
        string ProductId,
        string Name,
        string Icon,
        string Description,
        string State,
        IReadOnlyList<ScanDataSourceMetric> Metrics)
    {
        public string StateLabel => State switch
        {
            "missing" => "未検出",
            "empty" => "未取得",
            _ => "取得済み",
        };
    }

    private sealed record ScanDataSourceMetric(string Label, string Value);
}
