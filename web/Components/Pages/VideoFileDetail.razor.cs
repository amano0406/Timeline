using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Components.Shared;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class VideoFileDetail
{
    private const string VideoElementId = "video-detail-player";
    private const string TranscriptScrollElementId = "video-transcript-scroll";

    [SupplyParameterFromQuery(Name = "path")]
    public string SourcePath { get; set; } = "";

    private VideoFileDetailResult? _detail;
    private TimelineItemSummary? _itemSummary;
    private bool _loading = true;
    private bool _itemSummaryLoading;
    private string? _error;
    private double? _activeTurnStartSec;
    private bool _videoPlaying;
    private DotNetObjectReference<VideoFileDetail>? _videoDotNetRef;
    private bool _videoWatchAttached;
    private VideoFrameObservation? _selectedFrame;

    private IReadOnlyList<VideoTimelineTurn> DisplayTurns => BuildDisplayTurns();

    private IReadOnlyList<VideoTimelineEntry> TimelineEntries => BuildTimelineEntries();

    private IReadOnlyList<TextVolumeChartPoint> TextVolumePoints =>
        TextVolumeChartBuilder.Build(
            DisplayTurns.Select(turn => new TextVolumeSegment(turn.StartSec, turn.EndSec, turn.Text)),
            _detail?.File?.DurationSec);

    private string ActivityActiveStyle
    {
        get
        {
            var total = (_detail?.Activity.ActiveSec ?? 0) + (_detail?.Activity.InactiveSec ?? 0);
            if (total <= 0)
            {
                return "width:0%;";
            }

            var width = Math.Clamp((_detail!.Activity.ActiveSec / total) * 100, 0, 100);
            return FormattableString.Invariant($"width:{width:0.##}%;");
        }
    }

    private string ActivityInactiveStyle
    {
        get
        {
            var total = (_detail?.Activity.ActiveSec ?? 0) + (_detail?.Activity.InactiveSec ?? 0);
            if (total <= 0)
            {
                return "width:0%;";
            }

            var width = Math.Clamp((_detail!.Activity.InactiveSec / total) * 100, 0, 100);
            return FormattableString.Invariant($"width:{width:0.##}%;");
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        await UnwatchVideoAsync();
        _loading = true;
        _itemSummaryLoading = false;
        _error = null;
        _detail = null;
        _itemSummary = null;
        _activeTurnStartSec = null;
        _videoPlaying = false;
        _selectedFrame = null;

        try
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                _error = "動画ファイルが指定されていません。";
                return;
            }

            _detail = await Timeline.GetVideoFileDetailAsync(SourcePath);
            if (_detail.Available)
            {
                if (_detail.File is not null)
                {
                    await LoadItemSummaryAsync("video", _detail.File.ItemId);
                }

                return;
            }

            _error = string.IsNullOrWhiteSpace(_detail.Message)
                ? "指定された動画ファイルは見つかりませんでした。"
                : _detail.Message;
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

    private async Task LoadItemSummaryAsync(string product, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            _itemSummary = new TimelineItemSummary { Product = product, Message = "素材概要の対象が指定されていません。" };
            return;
        }

        _itemSummaryLoading = true;
        try
        {
            _itemSummary = await Timeline.GetTimelineItemSummaryAsync(product, itemId);
        }
        finally
        {
            _itemSummaryLoading = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_videoWatchAttached && _detail?.VideoAvailable == true)
        {
            _videoDotNetRef ??= DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("timelineAudioPlayer.watch", VideoElementId, _videoDotNetRef);
            _videoWatchAttached = true;
        }
    }

    private async Task SeekAsync(double seconds)
    {
        if (_detail?.VideoAvailable != true)
        {
            return;
        }

        _activeTurnStartSec = FindActiveTurnStart(seconds);
        _videoPlaying = true;
        StateHasChanged();
        await JS.InvokeVoidAsync("timelineAudioPlayer.seek", VideoElementId, Math.Max(0, seconds));

        if (_activeTurnStartSec is not null)
        {
            await ScrollTranscriptToTurnAsync(_activeTurnStartSec.Value);
        }
    }

    [JSInvokable]
    public async Task OnAudioTimeChanged(double currentTime)
    {
        var nextStart = FindActiveTurnStart(currentTime);
        if (nextStart == _activeTurnStartSec)
        {
            return;
        }

        _activeTurnStartSec = nextStart;
        await InvokeAsync(StateHasChanged);
        if (nextStart is not null)
        {
            await ScrollTranscriptToTurnAsync(nextStart.Value);
        }
    }

    [JSInvokable]
    public async Task OnAudioPlaybackStateChanged(bool playing)
    {
        if (_videoPlaying == playing)
        {
            return;
        }

        _videoPlaying = playing;
        await InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        await UnwatchVideoAsync();
        _videoDotNetRef?.Dispose();
        _videoDotNetRef = null;
    }

    private async Task UnwatchVideoAsync()
    {
        if (!_videoWatchAttached)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("timelineAudioPlayer.unwatch", VideoElementId);
        }
        catch
        {
        }
        finally
        {
            _videoWatchAttached = false;
        }
    }

    private IReadOnlyList<VideoTimelineTurn> BuildDisplayTurns()
    {
        if (_detail?.AudioVerbalizationResult.Turns.Count > 0)
        {
            return _detail.AudioVerbalizationResult.Turns
                .OrderBy(turn => turn.StartSec)
                .ThenBy(turn => turn.EndSec)
                .Select(turn => new VideoTimelineTurn(
                    turn.Index,
                    turn.StartSec,
                    turn.EndSec,
                    turn.Speaker,
                    turn.Text,
                    turn.Confidence,
                    turn.Status))
                .ToList();
        }

        return _detail?.Turns
            .OrderBy(turn => turn.StartSec)
            .ThenBy(turn => turn.EndSec)
            .Select(turn => new VideoTimelineTurn(
                turn.Index,
                turn.StartSec,
                turn.EndSec,
                turn.Speaker,
                turn.Text,
                turn.Confidence,
                "source_transcript"))
            .ToList() ?? [];
    }

    private IReadOnlyList<VideoTimelineEntry> BuildTimelineEntries()
    {
        var turns = DisplayTurns.ToList();
        var frames = _detail?.Frames
            .OrderBy(frame => frame.TimeSec)
            .ToList() ?? [];
        if (turns.Count == 0)
        {
            return frames.Select(frame => new VideoTimelineEntry(null, frame)).ToList();
        }

        var entries = new List<VideoTimelineEntry>();
        var frameIndex = 0;
        foreach (var turn in turns)
        {
            while (frameIndex < frames.Count && frames[frameIndex].TimeSec < turn.StartSec)
            {
                entries.Add(new VideoTimelineEntry(null, frames[frameIndex]));
                frameIndex++;
            }

            entries.Add(new VideoTimelineEntry(turn, null));

            while (frameIndex < frames.Count && frames[frameIndex].TimeSec <= turn.EndSec)
            {
                entries.Add(new VideoTimelineEntry(null, frames[frameIndex]));
                frameIndex++;
            }
        }

        while (frameIndex < frames.Count)
        {
            entries.Add(new VideoTimelineEntry(null, frames[frameIndex]));
            frameIndex++;
        }

        return entries;
    }

    private double? FindActiveTurnStart(double currentTime)
    {
        foreach (var turn in DisplayTurns)
        {
            if (currentTime >= turn.StartSec && currentTime < Math.Max(turn.StartSec, turn.EndSec))
            {
                return turn.StartSec;
            }
        }

        return null;
    }

    private async Task ScrollTranscriptToTurnAsync(double startSec)
    {
        try
        {
            await JS.InvokeVoidAsync(
                "timelineAudioPlayer.scrollTurnIntoView",
                TranscriptScrollElementId,
                TurnStartDataValue(startSec));
        }
        catch
        {
        }
    }

    private void OpenFrameModal(VideoFrameObservation frame)
    {
        _selectedFrame = frame;
    }

    private void CloseFrameModal()
    {
        _selectedFrame = null;
    }

    private static string DisplayName(VideoFileRow file) =>
        !string.IsNullOrWhiteSpace(file.FileName) ? file.FileName : EmptyText(file.RelativePath);

    private static string VideoSourceUrl(VideoFileRow file) =>
        $"/api/video/source?path={Uri.EscapeDataString(file.SourcePath)}";

    private static string VideoArtifactUrl(string path) =>
        $"/api/video/artifact?path={Uri.EscapeDataString(path)}";

    private static string StatusLabel(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "処理済み",
            "processing" => "処理中",
            "failed" => "エラー",
            _ => "未処理",
        };

    private static string StatusIcon(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "circle-check",
            "processing" => "spinner",
            "failed" => "triangle-exclamation",
            _ => "circle-minus",
        };

    private static string StatusPill(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "processing" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };

    private static string DurationLabel(double? seconds) =>
        seconds is >= 0 ? UiFormat.Duration(seconds.Value) : "-";

    private static string CountNumber(int count) =>
        count > 0 ? count.ToString("N0", CultureInfo.InvariantCulture) : "0";

    private static string CountLabel(int count) =>
        count > 0 ? $"{count.ToString("N0", CultureInfo.InvariantCulture)} 件" : "-";

    private static string TurnStartDataValue(double startSec) =>
        startSec.ToString("R", CultureInfo.InvariantCulture);

    private string TranscriptEntryClass(VideoTimelineTurn turn)
    {
        var active = _activeTurnStartSec is not null
            && Math.Abs(_activeTurnStartSec.Value - turn.StartSec) < 0.01;
        return active
            ? "tfa-video-timeline-entry tfa-video-turn-entry tfa-video-turn-active"
            : "tfa-video-timeline-entry tfa-video-turn-entry";
    }

    private string PlaybackIcon(double startSec) =>
        _videoPlaying
        && _activeTurnStartSec is not null
        && Math.Abs(_activeTurnStartSec.Value - startSec) < 0.01
            ? "pause"
            : "play";

    private static string TurnRangeLabel(VideoTimelineTurn turn) =>
        $"{WholeSecondDurationLabel(DisplayStartSecond(turn.StartSec))} - {WholeSecondDurationLabel(DisplayEndSecond(turn.StartSec, turn.EndSec))}";

    private static int DisplayStartSecond(double seconds)
    {
        if (seconds <= 0)
        {
            return 0;
        }

        var floor = Math.Floor(seconds);
        return Math.Abs(seconds - floor) < 0.001
            ? (int)floor
            : (int)Math.Ceiling(seconds);
    }

    private static int DisplayEndSecond(double startSec, double endSec)
    {
        var startSecond = DisplayStartSecond(startSec);
        return Math.Max(startSecond, (int)Math.Ceiling(Math.Max(startSec, endSec)) - 1);
    }

    private static string WholeSecondDurationLabel(int seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string SpeakerLabel(string value) =>
        FormatSpeakerLabel(value);

    private static string FormatSpeakerLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return "不明";
        }

        const string prefix = "SPEAKER_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[prefix.Length..], CultureInfo.InvariantCulture, out var speakerIndex))
        {
            return $"話者 {speakerIndex + 1}";
        }

        return value;
    }

    private static string ReadableText(VideoTimelineTurn turn) =>
        string.IsNullOrWhiteSpace(turn.Text) ? "文字起こしはありません。" : turn.Text;

    private static string ConfidenceLabel(double? value) =>
        value is null ? "-" : value.Value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string LevelLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.ToLowerInvariant() switch
            {
                "dark" => "暗め",
                "bright" => "明るめ",
                "normal" => "通常",
                "low" => "低い",
                "medium" => "中",
                "high" => "高い",
                _ => value,
            };

    private static string GridLabel(VideoFrameObservation frame)
    {
        var count = frame.Visual.Grid.Count;
        if (count <= 0)
        {
            return "-";
        }

        var rows = frame.Visual.Grid.Select(cell => cell.Row).DefaultIfEmpty(0).Max() + 1;
        var cols = frame.Visual.Grid.Select(cell => cell.Col).DefaultIfEmpty(0).Max() + 1;
        return rows > 0 && cols > 0 ? $"{rows} x {cols}" : $"{count} 区画";
    }

    private static string PaletteStyle(VideoColorPaletteEntry entry)
    {
        var color = string.IsNullOrWhiteSpace(entry.Hex) ? "#e2e8f0" : entry.Hex;
        return $"background:{color};";
    }

    private static string ColorLabel(VideoColorPaletteEntry entry)
    {
        var ratio = entry.Ratio is double value
            ? FormattableString.Invariant($"{value * 100:0}%")
            : "-";
        return $"{EmptyText(entry.Hex)} / {ratio}";
    }

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private sealed record VideoTimelineTurn(
        int Index,
        double StartSec,
        double EndSec,
        string Speaker,
        string Text,
        double? Confidence,
        string Status);

    private sealed record VideoTimelineEntry(
        VideoTimelineTurn? Turn,
        VideoFrameObservation? Frame);
}
