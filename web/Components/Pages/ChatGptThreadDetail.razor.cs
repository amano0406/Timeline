using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGptThreadDetail
{
    [Parameter]
    public string? ItemId { get; set; }

    private Timeline.Web.Services.ChatGptThreadDetail? _thread;
    private TimelineAppSettings? _timelineSettings;
    private bool _loading = true;
    private string? _error;

    private string TitleLabel => string.IsNullOrWhiteSpace(_thread?.Title) ? "スレッド詳細" : _thread.Title;
    private string DisplayTimeZoneId => _timelineSettings?.TimeZoneId ?? "Asia/Tokyo";
    private IReadOnlyList<MessageGroup> MessageGroups => BuildMessageGroups(_thread?.Messages ?? []);

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _error = null;
        _thread = null;

        if (string.IsNullOrWhiteSpace(ItemId))
        {
            _error = "スレッドが指定されていません。";
            _loading = false;
            return;
        }

        try
        {
            var threadTask = Timeline.GetChatGptThreadAsync(ItemId);
            var settingsTask = Timeline.GetTimelineSettingsAsync();
            await Task.WhenAll(threadTask, settingsTask);
            _thread = await threadTask;
            _timelineSettings = await settingsTask;
            if (!_thread.Available && !string.IsNullOrWhiteSpace(_thread.Message))
            {
                _error = _thread.Message;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private static string MessageClass(string? role) =>
        (role ?? "").Trim().ToLowerInvariant() switch
        {
            "user" => "tfa-chat-message tfa-chat-message-user",
            "assistant" => "tfa-chat-message tfa-chat-message-assistant",
            "system" or "developer" => "tfa-chat-message tfa-chat-message-system",
            _ => "tfa-chat-message tfa-chat-message-other",
        };

    private static string RoleLabel(string? role) =>
        (role ?? "").Trim().ToLowerInvariant() switch
        {
            "user" => "ユーザー",
            "assistant" => "ChatGPT",
            "system" => "システム",
            "developer" => "開発者",
            "tool" => "ツール",
            "" => "不明",
            var value => value,
        };

    private static string RoleIcon(string? role) =>
        (role ?? "").Trim().ToLowerInvariant() switch
        {
            "user" => "user",
            "assistant" => "robot",
            "system" or "developer" => "gear",
            "tool" => "wrench",
            _ => "message",
        };

    private static string EmptyText(string? value, string fallback = "-") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private string ShortDate(string? value) =>
        UiFormat.ShortDate(value ?? "", DisplayTimeZoneId);

    private static IReadOnlyList<MessageGroup> BuildMessageGroups(IReadOnlyList<TimelineThreadMessage> messages)
    {
        var groups = new List<MessageGroup>();
        foreach (var message in messages)
        {
            var role = (message.Role ?? "").Trim();
            if (groups.Count == 0 || !groups[^1].Role.Equals(role, StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(new MessageGroup(role, []));
            }

            groups[^1].Messages.Add(message);
        }

        return groups;
    }

    private static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "-";
        }

        var trimmed = path.Trim().TrimEnd('\\', '/');
        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return separator >= 0 && separator + 1 < trimmed.Length ? trimmed[(separator + 1)..] : trimmed;
    }

    private sealed record MessageGroup(string Role, List<TimelineThreadMessage> Messages);
}
