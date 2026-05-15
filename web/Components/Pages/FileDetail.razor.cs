using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class FileDetail
{
    [SupplyParameterFromQuery(Name = "sourceId")]
    public string? SourceId { get; set; }

    [SupplyParameterFromQuery(Name = "path")]
    public string? RelativePath { get; set; }

    private const string AudioElementId = "audio-detail-player";
    private AudioFileDetailResult? _detail;
    private AudioVerbalizationResult? _verbalizationResult;
    private AudioFileRow? _file;
    private string? _error;
    private string? _operationMessage;
    private double? _activeTurnStartSec;
    private DotNetObjectReference<FileDetail>? _audioDotNetRef;
    private bool _audioWatchAttached;
    private bool _startingVerbalization;
    private CancellationTokenSource? _verbalizationPollingCts;
    private Task? _verbalizationPollingTask;

    private bool HasVerbalizedTurns => _verbalizationResult?.Turns.Count > 0;

    protected override async Task OnParametersSetAsync()
    {
        await UnwatchAudioAsync();
        _error = null;
        _operationMessage = null;
        _detail = null;
        _verbalizationResult = null;
        _file = null;
        _activeTurnStartSec = null;
        CancelVerbalizationPolling();
        if (string.IsNullOrWhiteSpace(SourceId) || string.IsNullOrWhiteSpace(RelativePath))
        {
            _error = "音声ファイルが指定されていません。";
            return;
        }

        try
        {
            _detail = await Timeline.GetAudioFileDetailAsync(SourceId, RelativePath);
            if (_detail.Available && _detail.File is not null)
            {
                _file = _detail.File;
                await LoadVerbalizationResultAsync();
                StartVerbalizationPollingIfNeeded();
                return;
            }

            _error = string.IsNullOrWhiteSpace(_detail.Message)
                ? "指定された音声ファイルは見つかりませんでした。"
                : _detail.Message;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_audioWatchAttached && _detail?.AudioAvailable == true)
        {
            _audioDotNetRef ??= DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("timelineAudioPlayer.watch", AudioElementId, _audioDotNetRef);
            _audioWatchAttached = true;
        }
    }


}
