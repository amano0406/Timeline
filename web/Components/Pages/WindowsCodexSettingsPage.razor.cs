using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class WindowsCodexSettingsPage
{
    [Parameter] public bool Embedded { get; set; }

    private WindowsCodexOverview? _overview;
    private string _outputRoot = "";
    private string _outputRootDisplay = "";
    private bool _loading = true;
    private bool _saving;
    private bool _saved;
    private string? _error;

    private WindowsCodexSettings Settings => _overview?.Settings ?? new WindowsCodexSettings();

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _overview = await Timeline.GetWindowsCodexOverviewAsync();
            _outputRoot = string.IsNullOrWhiteSpace(Settings.OutputsRoot) ? "" : Settings.OutputsRoot;
            _outputRootDisplay = string.IsNullOrWhiteSpace(Settings.OutputsRootDisplayPath) ? _outputRoot : Settings.OutputsRootDisplayPath;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task PickOutputRootAsync()
    {
        _error = null;
        _saved = false;
        try
        {
            var path = await Js.InvokeAsync<string?>("timelineDirectoryPicker.pick", "出力ディレクトリを選択", _outputRootDisplay);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _outputRoot = path;
            _outputRootDisplay = path;
        }
        catch (JSException ex)
        {
            _error = ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        _saving = true;
        _saved = false;
        _error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_outputRoot))
            {
                throw new InvalidOperationException("出力ディレクトリを設定してください。");
            }

            _overview = await Timeline.SaveWindowsCodexSettingsAsync(new WindowsCodexSettingsSaveRequest
            {
                OutputsRoot = _outputRoot,
                OutputRoot = _outputRoot,
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

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
