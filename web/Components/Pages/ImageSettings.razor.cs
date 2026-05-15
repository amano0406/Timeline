using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ImageSettings
{
    [Parameter] public bool Embedded { get; set; }

    private ImageOverview? _overview;
    private readonly List<ImageInputRoot> _inputRoots = [];
    private ImageDirectoryRoot _outputRoot = new() { Id = "output", DisplayName = "Output" };
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
            _overview = await Timeline.GetImageOverviewAsync();
            _inputRoots.Clear();
            foreach (var root in _overview.Settings.InputRoots)
            {
                _inputRoots.Add(CloneRoot(root));
            }

            if (_inputRoots.Count == 0)
            {
                _inputRoots.Add(new ImageInputRoot
                {
                    Id = "input-1",
                    DisplayName = "Input",
                    Enabled = true,
                });
            }

            _outputRoot = CloneRoot(_overview.Settings.OutputRoot);
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

    private async Task AddInputRootAsync()
    {
        var path = await PickDirectoryAsync("入力ディレクトリを選択", LastInputPath());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_inputRoots.Any(root => root.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            _error = "同じ入力ディレクトリが既にあります。";
            return;
        }

        _inputRoots.Add(new ImageInputRoot
        {
            Id = $"input-{_inputRoots.Count + 1}",
            DisplayName = "Input",
            Path = path,
            DisplayPath = path,
            Enabled = true,
        });
        _saved = false;
    }

    private void RemoveInputRoot(ImageInputRoot root)
    {
        if (_inputRoots.Count <= 1)
        {
            return;
        }

        _inputRoots.Remove(root);
        _saved = false;
    }

    private async Task PickOutputRootAsync()
    {
        var path = await PickDirectoryAsync("出力ディレクトリを選択", DirectoryDisplay(_outputRoot));
        if (string.IsNullOrWhiteSpace(path))
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
            if (_inputRoots.Count == 0 || _inputRoots.Any(root => string.IsNullOrWhiteSpace(root.Path)))
            {
                throw new InvalidOperationException("入力ディレクトリを設定してください。");
            }
            if (string.IsNullOrWhiteSpace(_outputRoot.Path))
            {
                throw new InvalidOperationException("出力ディレクトリを設定してください。");
            }
            _overview = await Timeline.SaveImageSettingsAsync(new ImageSettingsSaveRequest
            {
                InputRoots = _inputRoots.Select(CloneRoot).ToList(),
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

    private string LastInputPath() =>
        _inputRoots.LastOrDefault(root => !string.IsNullOrWhiteSpace(root.Path))?.Path
        ?? "";

    private static ImageInputRoot CloneRoot(ImageInputRoot root) => new()
    {
        Id = root.Id,
        DisplayName = root.DisplayName,
        Path = root.Path,
        DisplayPath = root.DisplayPath,
        Enabled = root.Enabled,
        Exists = root.Exists,
    };

    private static ImageDirectoryRoot CloneRoot(ImageDirectoryRoot root) => new()
    {
        Id = root.Id,
        DisplayName = root.DisplayName,
        Path = root.Path,
        DisplayPath = root.DisplayPath,
        Exists = root.Exists,
    };

    private static string DirectoryDisplay(ImageInputRoot root) =>
        !string.IsNullOrWhiteSpace(root.DisplayPath) ? root.DisplayPath : EmptyText(root.Path);

    private static string DirectoryDisplay(ImageDirectoryRoot root) =>
        !string.IsNullOrWhiteSpace(root.DisplayPath) ? root.DisplayPath : EmptyText(root.Path);

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
