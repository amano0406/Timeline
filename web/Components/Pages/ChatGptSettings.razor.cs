using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGptSettings
{
    [Parameter] public bool Embedded { get; set; }

    private ChatGptOverview? _overview;
    private ChatGptDirectoryRoot _outputRoot = new() { Id = "output", DisplayName = "Output" };
    private bool _loading = true;
    private bool _saving;
    private bool _saved;
    private string? _error;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _overview = await Timeline.GetChatGptOverviewAsync();
            _outputRoot = CloneRoot(_overview.OutputRoot);
            if (string.IsNullOrWhiteSpace(_outputRoot.Path))
            {
                _outputRoot.DisplayPath = _outputRoot.Path;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task PickOutputRootAsync()
    {
        var path = await PickDirectoryAsync("出力ディレクトリを選択", DirectoryDisplay(_outputRoot));
        if (path is null)
        {
            return;
        }

        _outputRoot.Path = path;
        _outputRoot.DisplayPath = path;
        _saved = false;
    }

    private async Task<string?> PickDirectoryAsync(string title, string initialPath)
    {
        _error = null;
        try
        {
            return await Js.InvokeAsync<string?>("timelineDirectoryPicker.pick", title, initialPath);
        }
        catch (JSException ex)
        {
            _error = ex.Message;
            return null;
        }
    }

    private async Task SaveAsync()
    {
        _saving = true;
        _saved = false;
        _error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_outputRoot.Path))
            {
                throw new InvalidOperationException("出力ディレクトリを設定してください。");
            }

            _overview = await Timeline.SaveChatGptSettingsAsync(new ChatGptSettingsSaveRequest
            {
                OutputRoot = CloneRoot(_outputRoot),
                OutputRootPath = _outputRoot.Path,
            });
            _saved = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    private static ChatGptDirectoryRoot CloneRoot(ChatGptDirectoryRoot root) => new()
    {
        Id = root.Id,
        DisplayName = root.DisplayName,
        Path = root.Path,
        DisplayPath = root.DisplayPath,
        Exists = root.Exists,
    };

    private static string DirectoryDisplay(ChatGptDirectoryRoot root) =>
        !string.IsNullOrWhiteSpace(root.DisplayPath) ? root.DisplayPath : EmptyText(root.Path);

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
