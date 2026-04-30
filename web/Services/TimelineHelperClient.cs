using System.Net.Http.Json;
using System.Text.Json;

namespace Timeline.Web.Services;

public sealed class TimelineHelperClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<TimelineHelperClient> _logger;

    public TimelineHelperClient(HttpClient http, ILogger<TimelineHelperClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _http.GetFromJsonAsync<HelperHealth>("health", JsonOptions, cancellationToken);
            return health?.Ok == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeline helper health check failed.");
            return false;
        }
    }

    public async Task<TimelineProductOverview> GetAudioOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TimelineProductOverview>(
                    "products/audio/overview",
                    JsonOptions,
                    cancellationToken)
                ?? OfflineOverview("補助サーバーから状態を取得できませんでした。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForAudio overview.");
            return OfflineOverview("補助サーバーに接続できません。start.bat から起動してください。");
        }
    }

    public async Task<AudioFileListResult> GetAudioFilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AudioFileListResult>(
                    "products/audio/files",
                    JsonOptions,
                    cancellationToken)
                ?? new AudioFileListResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TimelineForAudio files.");
            return new AudioFileListResult();
        }
    }

    public async Task<TimelineProductOverview> SaveAudioSettingsAsync(
        AudioSettingsSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("products/audio/settings", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TimelineProductOverview>(JsonOptions, cancellationToken)
            ?? OfflineOverview("設定を保存しましたが、状態を読み取れませんでした。");
    }

    private static TimelineProductOverview OfflineOverview(string message) => new()
    {
        ProductFound = false,
        ProductPath = @"C:\apps\TimelineForAudio",
        WorkerState = "未確認",
        Message = message,
    };
}
