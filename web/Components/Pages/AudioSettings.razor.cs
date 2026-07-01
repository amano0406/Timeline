using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class AudioSettings
{
    [Parameter] public bool Embedded { get; set; }

    private TimelineProductOverview? _overview;
    private readonly List<RootRow> _inputRoots = [];
    private RootRow _outputRoot = new() { Id = "master", DisplayName = "TimelineForAudio Master", Enabled = true };
    private string _computeMode = "cpu";
    private string? _tokenDraft;
    private string? _tokenToSave;
    private string? _tokenValidationError;
    private string? _error;
    private bool _saved;
    private bool _saving;
    private bool _loading = true;
    private bool _tokenModalOpen;
    private RootRow? _removeTarget;
    private AudioModelInventoryResult? _modelInventory;
    private bool _modelsLoading;

    private string TokenDisplay =>
        _tokenToSave is { Length: > 0 }
            ? MaskToken(_tokenToSave)
            : _overview?.HasToken == true
                ? _overview.TokenPreview
                : "未設定";

    private bool HardwareDevicesLoaded => _overview is not null;
    private IReadOnlyList<string> CpuDevices => _overview?.CpuDevices ?? [];
    private IReadOnlyList<string> GpuDevices => _overview?.GpuDevices ?? [];
    private IReadOnlyList<AudioModelRow> HuggingFaceModels =>
        _modelInventory?.Models
            .Where(model => model.Source.Equals("huggingface", StringComparison.OrdinalIgnoreCase))
            .ToList()
        ?? [];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await LoadAsync();
        StateHasChanged();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            var settings = await Timeline.GetTimelineSettingsAsync();
            _overview = await Timeline.GetAudioOverviewAsync();
            _computeMode = ComputeModeResolver.ResolveProduct(_overview.ComputeMode, settings.CommonAi);
            _inputRoots.Clear();
            _inputRoots.AddRange(_overview.InputRoots.Select(CloneRoot));
            if (_inputRoots.Count == 0)
            {
                _inputRoots.Add(new RootRow { Id = "audio-1", DisplayName = "Audio", Enabled = true });
            }

            _outputRoot = CloneRoot(_overview.OutputRoot ?? new RootRow
            {
                Id = "master",
                DisplayName = "TimelineForAudio Master",
                Enabled = true,
            });

            _loading = false;
            _modelsLoading = true;
            StateHasChanged();
            _modelInventory = await Timeline.GetAudioModelsAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _modelsLoading = false;
            _loading = false;
        }
    }

    private void OpenTokenModal()
    {
        _tokenDraft = "";
        _tokenValidationError = null;
        _tokenModalOpen = true;
    }

    private void CloseTokenModal()
    {
        _tokenModalOpen = false;
        _tokenDraft = null;
        _tokenValidationError = null;
    }

    private void SaveTokenDraft()
    {
        var token = _tokenDraft?.Trim() ?? "";
        if (!ValidateTokenFormat(token, out var message))
        {
            _tokenValidationError = message;
            return;
        }

        _tokenToSave = token;
        CloseTokenModal();
    }

    private async Task OpenAddInputDirectory()
    {
        var path = await PickDirectoryAsync("入力ディレクトリを選択", "");
        if (path is null)
        {
            return;
        }

        _inputRoots.Add(new RootRow
        {
            Id = NextInputRootId(),
            DisplayName = DirectoryNameFromPath(path),
            Path = path,
            Enabled = true,
        });
    }

    private async Task OpenOutputDirectory()
    {
        var path = await PickDirectoryAsync("出力ディレクトリを選択", _outputRoot.Path);
        if (path is null)
        {
            return;
        }

        _outputRoot.Path = path;
        _outputRoot.DisplayName = DirectoryNameFromPath(path);
    }

    private async Task<string?> PickDirectoryAsync(string title, string initialPath)
    {
        _error = null;
        _saved = false;
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

    private void AskRemoveInputRoot(RootRow root) => _removeTarget = root;

    private void CancelRemoveInputRoot() => _removeTarget = null;

    private void ConfirmRemoveInputRoot()
    {
        if (_removeTarget is not null && _inputRoots.Count > 1)
        {
            _inputRoots.Remove(_removeTarget);
        }
        _removeTarget = null;
    }

    private async Task SaveAsync()
    {
        _saving = true;
        _saved = false;
        _error = null;
        try
        {
            Validate();
            _overview = await Timeline.SaveAudioSettingsAsync(new AudioSettingsSaveRequest
            {
                Token = _tokenToSave,
                ComputeMode = ComputeModeResolver.NormalizeProduct(_computeMode),
                InputRoots = _inputRoots
                    .Where(root => !string.IsNullOrWhiteSpace(root.Path))
                    .Select(CloneRoot)
                    .ToList(),
                OutputRoot = CloneRoot(_outputRoot),
                OutputPath = _outputRoot.Path,
            });
            _tokenToSave = null;
            _saved = true;
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

    private void Validate()
    {
        if (_tokenToSave is { Length: > 0 } && !ValidateTokenFormat(_tokenToSave, out var tokenMessage))
        {
            throw new InvalidOperationException(tokenMessage);
        }
        if (_inputRoots.Count == 0 || _inputRoots.All(root => string.IsNullOrWhiteSpace(root.Path)))
        {
            throw new InvalidOperationException("入力ディレクトリを1件以上設定してください。");
        }
        if (string.IsNullOrWhiteSpace(_outputRoot.Path))
        {
            throw new InvalidOperationException("出力ディレクトリを設定してください。");
        }
    }

    private string NextInputRootId()
    {
        var index = _inputRoots.Count + 1;
        var candidate = $"audio-{index}";
        while (_inputRoots.Any(root => root.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            candidate = $"audio-{index}";
        }
        return candidate;
    }

    private static RootRow CloneRoot(RootRow root) => new()
    {
        Id = root.Id,
        DisplayName = root.DisplayName,
        Path = root.Path,
        Enabled = root.Enabled,
    };

    private static bool ValidateTokenFormat(string token, out string message)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            message = "トークンを入力してください。";
            return false;
        }
        if (!token.StartsWith("hf_", StringComparison.Ordinal) || token.Length < 12)
        {
            message = "Hugging Face token の形式を確認してください。";
            return false;
        }

        message = "";
        return true;
    }

    private static string DirectoryDisplay(string path) =>
        string.IsNullOrWhiteSpace(path) ? "未設定" : path;

    private static string ModelRoleLabel(AudioModelRow model) => model.Role switch
    {
        "speaker_diarization" => "話者分離",
        "acoustic_unit_extraction" => "音響単位抽出",
        "speech_candidate_detection" => "発話候補検出",
        _ => string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelId : model.DisplayName,
    };

    private static string DirectoryNameFromPath(string path, string fallback = "Directory")
    {
        var text = path.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        var separator = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/'));
        if (separator >= 0 && separator + 1 < text.Length)
        {
            return text[(separator + 1)..];
        }
        return text.Length == 2 && text[1] == ':' ? text : fallback;
    }

    private static string MaskToken(string token)
    {
        var value = token.Trim();
        if (value.Length <= 8)
        {
            return new string('•', value.Length);
        }
        return $"{value[..4]}{new string('•', Math.Max(4, value.Length - 8))}{value[^4..]}";
    }
}
