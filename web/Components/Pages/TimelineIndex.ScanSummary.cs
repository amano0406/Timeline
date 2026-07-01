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

    private void QueueScanDataSourcesLoad()
    {
        if (_disposed || _loadingScanDataSources)
        {
            return;
        }

        _ = LoadScanDataSourcesAsync(_pollingCts?.Token ?? CancellationToken.None);
    }

    private async Task LoadScanDataSourcesAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _loadingScanDataSources || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _loadingScanDataSources = true;
        await InvokeAsync(StateHasChanged);
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

            var audioTask = Timeline.GetAudioOverviewAsync(cancellationToken);
            var windowsTask = Timeline.GetWindowsCodexOverviewAsync(cancellationToken);
            var chatGptTask = Timeline.GetChatGptOverviewAsync(cancellationToken);
            var imageTask = Timeline.GetImageOverviewAsync(cancellationToken);
            var videoTask = Timeline.GetVideoOverviewAsync(cancellationToken: cancellationToken);
            var pcTask = Timeline.GetPcOverviewAsync(cancellationToken);

            await Task.WhenAll(audioTask, windowsTask, chatGptTask, imageTask, videoTask, pcTask);

            cancellationToken.ThrowIfCancellationRequested();

            _audio = await audioTask;
            _windowsCodex = await windowsTask;
            _chatGpt = await chatGptTask;
            _image = await imageTask;
            _video = await videoTask;
            _pc = await pcTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loadingScanDataSources = false;
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private static string FormatNumber(int value) => value.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"));

    private static string FormatNumber(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"));
}
