using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

var options = WindowsInstallerOptions.Parse(args);
if (options.ShowHelp)
{
    PrintHelp();
    return 0;
}

if (!OperatingSystem.IsWindows())
{
    WriteError("Timeline Windows Installer is available only on Windows.", options.JsonOutput);
    return 2;
}

try
{
    if (options.UninstallWorker)
    {
        return RunUninstallWorker(options);
    }

    if (options.Uninstall)
    {
        return RunUninstallWizard(options);
    }

    if (args.Length == 0 || options.Wizard)
    {
        var wizardInstallDirectory = ResolveInstallDirectory(options.InstallDirectory);
        TimelineWindowsInstallResult wizardPlan;
        try
        {
            var wizardArtifactPath = ResolveArtifactPath(options.ArtifactPath);
            wizardPlan = BuildPlan(wizardArtifactPath, wizardInstallDirectory, options);
        }
        catch (Exception ex)
        {
            wizardPlan = new TimelineWindowsInstallResult
            {
                State = "planned",
                ArtifactPath = options.ArtifactPath ?? "",
                InstallDirectory = wizardInstallDirectory,
                Force = options.Force,
            };
            wizardPlan.Blockers.Add(ex.Message);
        }

        return RunInstallWizard(wizardPlan, options);
    }

    var artifactPath = ResolveArtifactPath(options.ArtifactPath);
    var installDirectory = ResolveInstallDirectory(options.InstallDirectory);
    var plan = BuildPlan(artifactPath, installDirectory, options);

    if (options.PlanOnly || options.DryRun)
    {
        WriteResult(plan, options.JsonOutput);
        return plan.Blockers.Count == 0 ? 0 : 1;
    }

    if (plan.Blockers.Count > 0)
    {
        WriteResult(plan, options.JsonOutput);
        return 1;
    }

    var result = Install(plan, options);
    WriteResult(result, options.JsonOutput);
    return result.Blockers.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    WriteError(ex.Message, options.JsonOutput);
    return 1;
}

static TimelineWindowsInstallResult Install(TimelineWindowsInstallResult plan, WindowsInstallerOptions options)
{
    var stagingRoot = Path.Combine(
        Path.GetTempPath(),
        "TimelineInstaller",
        DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
    var extractedRoot = Path.Combine(stagingRoot, "Timeline");

    try
    {
        Directory.CreateDirectory(stagingRoot);
        ZipFile.ExtractToDirectory(plan.ArtifactPath, stagingRoot);
        if (!Directory.Exists(extractedRoot))
        {
            throw new DirectoryNotFoundException("The artifact did not contain the expected Timeline root directory.");
        }

        if (Directory.Exists(plan.InstallDirectory))
        {
            if (!options.Force)
            {
                throw new InvalidOperationException("The install directory already exists. Use --force only when replacing application files is intended.");
            }

            DeleteReplaceableApplicationContent(plan.InstallDirectory);
        }
        else
        {
            Directory.CreateDirectory(plan.InstallDirectory);
        }

        CopyDirectory(extractedRoot, plan.InstallDirectory);
        var shortcut = TimelineLauncherShortcutService.Install(plan.InstallDirectory);
        var uninstall = TimelineWindowsUninstallRegistrationService.Register(plan.InstallDirectory);

        plan.State = shortcut.Registered && uninstall.Registered ? "installed" : "installed_with_registration_warnings";
        plan.Shortcut = shortcut;
        plan.UninstallRegistration = uninstall;
        plan.Messages.Add("Timeline application files were installed.");
        if (shortcut.Registered)
        {
            plan.Messages.Add("Start Menu shortcut was registered.");
        }
        else
        {
            plan.Warnings.Add(shortcut.Message);
        }

        if (uninstall.Registered)
        {
            plan.Messages.Add("Windows Apps & Features uninstall entry was registered.");
        }
        else
        {
            plan.Warnings.Add(uninstall.Message);
        }

        WriteInstallReceipt(plan);
        return plan;
    }
    finally
    {
        TryDeleteDirectory(stagingRoot);
    }
}

static int RunInstallWizard(TimelineWindowsInstallResult initialPlan, WindowsInstallerOptions initialOptions)
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    var exitCode = initialPlan.Blockers.Count == 0 ? 0 : 1;
    using var form = new Form
    {
        Text = "Timeline セットアップ",
        StartPosition = FormStartPosition.CenterScreen,
        MinimumSize = new Size(760, 560),
        Size = new Size(860, 620),
        Font = new Font("Yu Gothic UI", 10F),
    };

    var root = new TableLayoutPanel
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 6,
        Padding = new Padding(18),
    };
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    form.Controls.Add(root);

    root.Controls.Add(new Label
    {
        AutoSize = true,
        Font = new Font(form.Font, FontStyle.Bold),
        Text = "Timeline をインストールします",
        Margin = new Padding(0, 0, 0, 8),
    });
    root.Controls.Add(new Label
    {
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        Text = "アプリ本体を配置し、スタートメニューと Windows のアプリ一覧に登録します。既存の Timeline を更新する場合も、設定・素材・生成データ・ログ・実行状態は保持します。",
        Margin = new Padding(0, 0, 0, 14),
    });

    var artifactText = NewPathTextBox(initialPlan.ArtifactPath);
    var installText = NewPathTextBox(initialPlan.InstallDirectory);
    var replaceExisting = new CheckBox
    {
        AutoSize = true,
        Text = "既存の Timeline 本体を更新する",
        Checked = Directory.Exists(initialPlan.InstallDirectory),
        Margin = new Padding(0, 8, 0, 0),
    };

    root.Controls.Add(NewPathRow("製品ファイル", artifactText, "選択...", () =>
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Timeline の製品 ZIP を選択",
            Filter = "Timeline product (*.zip)|*.zip|All files (*.*)|*.*",
            FileName = artifactText.Text,
        };
        if (dialog.ShowDialog(form) == DialogResult.OK)
        {
            artifactText.Text = dialog.FileName;
        }
    }));

    root.Controls.Add(NewPathRow("インストール先", installText, "選択...", () =>
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Timeline のインストール先を選択",
            SelectedPath = installText.Text,
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(form) == DialogResult.OK)
        {
            installText.Text = dialog.SelectedPath;
        }
    }, replaceExisting));

    var statusBox = new TextBox
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        HideSelection = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 10, 0, 12),
    };
    root.Controls.Add(statusBox);

    var buttons = new FlowLayoutPanel
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.RightToLeft,
        AutoSize = true,
    };
    var closeButton = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
    var installButton = new Button { Text = "インストール", AutoSize = true };
    buttons.Controls.Add(closeButton);
    buttons.Controls.Add(installButton);
    root.Controls.Add(buttons);

    form.CancelButton = closeButton;
    closeButton.Click += (_, _) => form.Close();

    TimelineWindowsInstallResult currentPlan = initialPlan;
    WindowsInstallerOptions CurrentOptions()
        => initialOptions with
        {
            ArtifactPath = artifactText.Text,
            InstallDirectory = installText.Text,
            Force = replaceExisting.Checked,
            Wizard = false,
        };

    void RefreshPlan()
    {
        try
        {
            var options = CurrentOptions();
            currentPlan = BuildPlan(
                ResolveArtifactPath(options.ArtifactPath),
                ResolveInstallDirectory(options.InstallDirectory),
                options);

            statusBox.Text = FormatInstallPlanForWizard(currentPlan);
            installButton.Enabled = currentPlan.Blockers.Count == 0;
        }
        catch (Exception ex)
        {
            statusBox.Text = "インストール前の確認で問題が見つかりました。" + Environment.NewLine + ex.Message;
            installButton.Enabled = false;
        }
    }

    artifactText.TextChanged += (_, _) => RefreshPlan();
    installText.TextChanged += (_, _) =>
    {
        replaceExisting.Checked = Directory.Exists(installText.Text);
        RefreshPlan();
    };
    replaceExisting.CheckedChanged += (_, _) => RefreshPlan();

    installButton.Click += async (_, _) =>
    {
        installButton.Enabled = false;
        closeButton.Enabled = false;
        statusBox.Text = "インストールしています..." + Environment.NewLine;
        var options = CurrentOptions();
        try
        {
            var result = await Task.Run(() => Install(currentPlan, options));
            statusBox.Text = FormatInstallPlanForWizard(result);
            exitCode = result.Blockers.Count == 0 ? 0 : 1;
            MessageBox.Show(
                form,
                result.Blockers.Count == 0 ? "Timeline のインストールが完了しました。" : "Timeline のインストールに失敗しました。",
                "Timeline セットアップ",
                MessageBoxButtons.OK,
                result.Blockers.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            statusBox.Text += Environment.NewLine + ex.Message;
            exitCode = 1;
            MessageBox.Show(form, ex.Message, "Timeline セットアップ", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            closeButton.Enabled = true;
            installButton.Enabled = currentPlan.Blockers.Count == 0;
        }
    };

    RefreshPlan();
    statusBox.SelectionStart = 0;
    statusBox.SelectionLength = 0;
    Application.Run(form);
    return exitCode;
}

static int RunUninstallWizard(WindowsInstallerOptions options)
{
    var installDirectory = ResolveInstallDirectory(options.InstallDirectory);
    if (options.JsonOutput)
    {
        return StartUninstallWorker(installDirectory, showResultUi: false, jsonOutput: true);
    }

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    var exitCode = 1;
    using var form = new Form
    {
        Text = "Timeline アンインストール",
        StartPosition = FormStartPosition.CenterScreen,
        MinimumSize = new Size(660, 420),
        Size = new Size(720, 460),
        Font = new Font("Yu Gothic UI", 10F),
    };

    var root = new TableLayoutPanel
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 5,
        Padding = new Padding(18),
    };
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    form.Controls.Add(root);

    root.Controls.Add(new Label
    {
        AutoSize = true,
        Font = new Font(form.Font, FontStyle.Bold),
        Text = "Timeline をアンインストールします",
        Margin = new Padding(0, 0, 0, 8),
    });
    root.Controls.Add(new Label
    {
        AutoSize = true,
        MaximumSize = new Size(640, 0),
        Text = "既定ではアプリ本体だけを削除します。設定、素材、生成データ、ログ、Docker 関連の実行状態は残します。必要なデータを失わず、再インストールや調査を続けられるようにするためです。",
        Margin = new Padding(0, 0, 0, 14),
    });

    root.Controls.Add(new Label
    {
        AutoSize = true,
        Text = $"対象: {installDirectory}",
        Margin = new Padding(0, 0, 0, 14),
    });

    var info = new TextBox
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        HideSelection = true,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        Text = FormatUninstallSummary(installDirectory),
    };
    root.Controls.Add(info);

    var buttons = new FlowLayoutPanel
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.RightToLeft,
        AutoSize = true,
    };
    var cancelButton = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
    var uninstallButton = new Button { Text = "アンインストール", AutoSize = true };
    buttons.Controls.Add(cancelButton);
    buttons.Controls.Add(uninstallButton);
    root.Controls.Add(buttons);
    form.CancelButton = cancelButton;

    cancelButton.Click += (_, _) => form.Close();
    form.Shown += (_, _) =>
    {
        cancelButton.Focus();
        info.SelectionStart = 0;
        info.SelectionLength = 0;
    };
    uninstallButton.Click += (_, _) =>
    {
        var confirmation = MessageBox.Show(
            form,
            "Timeline 本体を削除します。設定・素材・生成データは保持します。続行しますか？",
            "Timeline アンインストール",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        exitCode = StartUninstallWorker(installDirectory, showResultUi: true, jsonOutput: false);
        form.Close();
    };

    Application.Run(form);
    return exitCode;
}

static int StartUninstallWorker(
    string installDirectory,
    bool showResultUi,
    bool jsonOutput,
    bool skipOsRegistration = false,
    string? logPath = null)
{
    var stagingRoot = Path.Combine(
        Path.GetTempPath(),
        "TimelineUninstaller",
        DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(stagingRoot);
    CopyDirectory(AppContext.BaseDirectory, stagingRoot);

    var executableName = Path.GetFileName(Environment.ProcessPath);
    if (string.IsNullOrWhiteSpace(executableName))
    {
        executableName = "Timeline.WindowsInstaller.exe";
    }

    var workerPath = Path.Combine(stagingRoot, executableName);
    if (!File.Exists(workerPath))
    {
        throw new FileNotFoundException("Uninstall worker could not be staged.", workerPath);
    }

    var resultPath = string.IsNullOrWhiteSpace(logPath)
        ? Path.Combine(stagingRoot, "timeline-uninstall-result.json")
        : Path.GetFullPath(logPath);
    var arguments = string.Join(
        " ",
        "--uninstall-worker",
        "--install-dir",
        QuoteArgument(installDirectory),
        "--parent-pid",
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
        "--log",
        QuoteArgument(resultPath),
        showResultUi ? "--result-ui" : "",
        skipOsRegistration ? "--skip-os-registration" : "",
        jsonOutput ? "--json" : "");

    Process.Start(new ProcessStartInfo
    {
        FileName = workerPath,
        Arguments = arguments,
        WorkingDirectory = stagingRoot,
        UseShellExecute = true,
        WindowStyle = showResultUi ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
    });

    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                state = "uninstall_started",
                installDirectory,
                workerPath,
                resultPath,
            },
            JsonOptions()));
    }

    return 0;
}

static int RunUninstallWorker(WindowsInstallerOptions options)
{
    var installDirectory = ResolveInstallDirectory(options.InstallDirectory);
    if (IsPathUnder(AppContext.BaseDirectory, installDirectory))
    {
        return StartUninstallWorker(
            installDirectory,
            options.ResultUi,
            options.JsonOutput,
            options.SkipOsRegistration,
            options.LogPath);
    }

    var result = new TimelineWindowsUninstallResult
    {
        State = "running",
        InstallDirectory = installDirectory,
        StartedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    };

    try
    {
        WaitForParentProcess(options.ParentPid);
        TryStopTimeline(installDirectory, result);
        if (options.SkipOsRegistration)
        {
            result.Warnings.Add("OS registration removal was skipped for verification.");
        }
        else
        {
            result.Shortcut = TimelineLauncherShortcutService.Remove(installDirectory);
            result.UninstallRegistration = TimelineWindowsUninstallRegistrationService.Remove(installDirectory);
        }

        DeleteReplaceableApplicationContent(installDirectory);
        TryDeleteEmptyDirectory(installDirectory);
        result.State = "removed";
        result.Messages.Add("Timeline application files were removed. User data, settings, logs, runtime state, and managed products were preserved.");
        result.CompletedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
    catch (Exception ex)
    {
        result.State = "failed";
        result.Error = ex.Message;
        result.CompletedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    WriteUninstallResult(result, options.LogPath);

    if (options.JsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
    }

    if (options.ResultUi)
    {
        MessageBox.Show(
            result.State == "removed"
                ? "Timeline のアンインストールが完了しました。設定・素材・生成データは保持されています。"
                : $"Timeline のアンインストールに失敗しました。{Environment.NewLine}{result.Error}",
            "Timeline アンインストール",
            MessageBoxButtons.OK,
            result.State == "removed" ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    return result.State == "removed" ? 0 : 1;
}

static TimelineWindowsInstallResult BuildPlan(string artifactPath, string installDirectory, WindowsInstallerOptions options)
{
    var result = new TimelineWindowsInstallResult
    {
        State = "planned",
        ArtifactPath = artifactPath,
        InstallDirectory = installDirectory,
        Force = options.Force,
    };

    if (!File.Exists(artifactPath))
    {
        result.Blockers.Add($"Artifact was not found: {artifactPath}");
        return result;
    }

    ValidateArtifactShape(artifactPath, result);
    ValidateInstallDirectory(installDirectory, options, result);
    return result;
}

static void ValidateArtifactShape(string artifactPath, TimelineWindowsInstallResult result)
{
    try
    {
        using var archive = ZipFile.OpenRead(artifactPath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requiredEntries = new[]
        {
            "Timeline/VERSION",
            "Timeline/launcher/Timeline.Launcher.exe",
            "Timeline/launcher-tray/Timeline.Launcher.Tray.exe",
            "Timeline/local-api/Timeline.LocalApi.exe",
            "Timeline/installer/Timeline.WindowsInstaller.exe",
            "Timeline/docker-compose.yml"
        };

        foreach (var requiredEntry in requiredEntries)
        {
            if (!entries.Contains(requiredEntry))
            {
                result.Blockers.Add($"Artifact is missing required entry: {requiredEntry}");
            }
        }

        foreach (var executableEntry in new[]
        {
            "Timeline/launcher/Timeline.Launcher.exe",
            "Timeline/launcher/Timeline.Launcher.dll",
            "Timeline/launcher-tray/Timeline.Launcher.Tray.exe",
            "Timeline/launcher-tray/Timeline.Launcher.Tray.dll",
            "Timeline/local-api/Timeline.LocalApi.exe",
            "Timeline/local-api/Timeline.LocalApi.dll",
            "Timeline/installer/Timeline.WindowsInstaller.exe",
            "Timeline/installer/Timeline.WindowsInstaller.dll"
        })
        {
            AddUnsignedWindowsBinaryWarning(archive, executableEntry, result.Warnings);
        }

        if (entries.Any(entry => entry.Contains("/settings.json", StringComparison.OrdinalIgnoreCase)))
        {
            result.Blockers.Add("Artifact must not contain settings.json.");
        }

        if (entries.Any(entry => entry.StartsWith("Timeline/data/", StringComparison.OrdinalIgnoreCase)))
        {
            result.Blockers.Add("Artifact must not contain Timeline user data.");
        }

        var versionEntry = archive.GetEntry("Timeline/VERSION");
        if (versionEntry is not null)
        {
            using var stream = versionEntry.Open();
            using var document = JsonDocument.Parse(stream);
            result.Version = document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString() ?? ""
                : "";
            result.RuntimeIdentifier = document.RootElement.TryGetProperty("runtimeIdentifier", out var runtime)
                ? runtime.GetString() ?? ""
                : "";

            if (!result.RuntimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
            {
                result.Blockers.Add($"Artifact runtime is not Windows: {result.RuntimeIdentifier}");
            }
        }
    }
    catch (InvalidDataException ex)
    {
        result.Blockers.Add($"Artifact is not a valid ZIP archive. {ex.Message}");
    }
    catch (JsonException ex)
    {
        result.Blockers.Add($"Artifact VERSION metadata could not be parsed. {ex.Message}");
    }
}

static void AddUnsignedWindowsBinaryWarning(ZipArchive archive, string entryName, ICollection<string> warnings)
{
    var entry = archive.Entries.FirstOrDefault(value =>
        string.Equals(value.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));
    if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
    {
        return;
    }

    var tempPath = Path.Combine(
        Path.GetTempPath(),
        $"timeline-signature-check-{Guid.NewGuid():N}{Path.GetExtension(entry.Name)}");
    try
    {
        entry.ExtractToFile(tempPath, overwrite: true);
        if (!HasAuthenticodeSignature(tempPath))
        {
            warnings.Add($"{entryName} is not Authenticode-signed. Smart App Control, WDAC, or Code Integrity can block this binary on constrained Windows environments.");
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
    {
        warnings.Add($"Execution trust could not be checked for {entryName}: {ex.Message}");
    }
    finally
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
        }
    }
}

static bool HasAuthenticodeSignature(string path)
{
    try
    {
#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        return certificate is not null;
    }
    catch (CryptographicException)
    {
        return false;
    }
}

static void ValidateInstallDirectory(string installDirectory, WindowsInstallerOptions options, TimelineWindowsInstallResult result)
{
    if (Path.GetPathRoot(installDirectory) == Path.GetFullPath(installDirectory))
    {
        result.Blockers.Add("ドライブ直下はインストール先にできません。専用フォルダーを選択してください。");
        return;
    }

    if (!Directory.Exists(installDirectory))
    {
        result.Messages.Add("インストール先フォルダーを作成します。");
        return;
    }

    var existingEntries = Directory.EnumerateFileSystemEntries(installDirectory).Take(2).ToArray();
    if (existingEntries.Length == 0)
    {
        result.Messages.Add("インストール先フォルダーは空です。");
        return;
    }

    if (options.Force)
    {
        if (!LooksLikeTimelineInstallDirectory(installDirectory))
        {
            result.Blockers.Add("インストール先にファイルがありますが、Timeline のアプリフォルダーとは判断できません。空のフォルダーか既存の Timeline フォルダーを選択してください。");
            return;
        }

        result.Warnings.Add("既存のアプリ本体を更新します。設定、素材、生成データ、ログ、実行状態、管理対象サブ製品は保持します。");
    }
    else
    {
        result.Blockers.Add("インストール先に既存ファイルがあります。既存の Timeline を更新する場合は、更新チェックを有効にしてください。");
    }
}

static bool LooksLikeTimelineInstallDirectory(string installDirectory)
{
    return File.Exists(Path.Combine(installDirectory, "VERSION"))
        || File.Exists(Path.Combine(installDirectory, "launcher", "Timeline.Launcher.exe"))
        || File.Exists(Path.Combine(installDirectory, "local-api", "Timeline.LocalApi.exe"))
        || File.Exists(Path.Combine(installDirectory, "runtime", "windows-install-receipt.json"));
}

static void DeleteReplaceableApplicationContent(string installDirectory)
{
    var preserveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "data",
        "logs",
        "runtime",
        "products",
        "settings.json"
    };

    foreach (var directory in Directory.EnumerateDirectories(installDirectory))
    {
        if (!preserveNames.Contains(Path.GetFileName(directory)))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    foreach (var file in Directory.EnumerateFiles(installDirectory))
    {
        if (!preserveNames.Contains(Path.GetFileName(file)))
        {
            File.Delete(file);
        }
    }
}

static void CopyDirectory(string sourceDirectory, string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var destinationPath = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
        File.Copy(file, destinationPath, overwrite: true);
    }
}

static TextBox NewPathTextBox(string text)
    => new()
    {
        Dock = DockStyle.Fill,
        Text = text,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 2, 8, 10),
    };

static Control NewPathRow(string label, TextBox textBox, string buttonText, Action browse, Control? extraControl = null)
{
    var panel = new TableLayoutPanel
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 2,
    };
    panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    panel.Controls.Add(new Label
    {
        Text = label,
        AutoSize = true,
        Font = new Font("Yu Gothic UI", 9F, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 2),
    }, 0, 0);
    panel.SetColumnSpan(panel.Controls[0], 2);
    panel.Controls.Add(textBox, 0, 1);
    var button = new Button
    {
        Text = buttonText,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 10),
    };
    button.Click += (_, _) => browse();
    panel.Controls.Add(button, 1, 1);

    if (extraControl is not null)
    {
        panel.Controls.Add(extraControl, 0, 2);
        panel.SetColumnSpan(extraControl, 2);
    }

    return panel;
}

static string FormatInstallPlanForWizard(TimelineWindowsInstallResult result)
{
    var lines = new List<string>
    {
        $"状態: {FormatInstallState(result.State)}",
        $"製品ファイル: {result.ArtifactPath}",
        $"インストール先: {result.InstallDirectory}",
    };
    if (!string.IsNullOrWhiteSpace(result.Version))
    {
        lines.Add($"バージョン: {result.Version}");
    }

    if (result.Messages.Count > 0)
    {
        lines.Add("");
        lines.Add("案内:");
        lines.AddRange(result.Messages.Select(message => $" - {FormatInstallMessage(message)}"));
    }

    if (result.Warnings.Count > 0)
    {
        lines.Add("");
        lines.Add("注意:");
        lines.AddRange(FormatWarningsForWizard(result.Warnings).Select(warning => $" - {warning}"));
    }

    if (result.Blockers.Count > 0)
    {
        lines.Add("");
        lines.Add("解消が必要な項目:");
        lines.AddRange(result.Blockers.Select(blocker => $" - {blocker}"));
    }
    else
    {
        lines.Add("");
        lines.Add("インストールを開始できます。");
    }

    return string.Join(Environment.NewLine, lines);
}

static string FormatInstallState(string state)
    => state switch
    {
        "planned" => "確認済み",
        "installed" => "インストール済み",
        "failed" => "失敗",
        _ => state
    };

static string FormatInstallMessage(string message)
    => message switch
    {
        "Install directory will be created." => "インストール先フォルダーを作成します。",
        "Install directory exists and is empty." => "インストール先フォルダーは空です。",
        "Timeline application files were installed." => "Timeline のアプリ本体を配置しました。",
        "Start Menu shortcut was registered." => "スタートメニューに Timeline の起動項目を登録しました。",
        "Windows Apps & Features uninstall entry was registered." => "Windows のアプリ一覧にアンインストール入口を登録しました。",
        _ => message
    };

static IEnumerable<string> FormatWarningsForWizard(IEnumerable<string> warnings)
{
    var warningList = warnings.ToList();
    var executionTrustWarnings = warningList
        .Where(IsWindowsExecutionTrustWarning)
        .ToList();

    if (executionTrustWarnings.Count > 0)
    {
        yield return $"Windows 実行信頼: 未署名の実行ファイルが {executionTrustWarnings.Count} 件あります。通常環境ではインストールできますが、Smart App Control / WDAC / Code Integrity が有効なPCでは起動を止められる可能性があります。署名対応は別チケットで扱います。";
    }

    foreach (var warning in warningList.Where(warning => !IsWindowsExecutionTrustWarning(warning)))
    {
        yield return FormatInstallMessage(warning);
    }
}

static bool IsWindowsExecutionTrustWarning(string warning)
    => warning.Contains("is not Authenticode-signed", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("Smart App Control", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("WDAC", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("Code Integrity", StringComparison.OrdinalIgnoreCase);

static string FormatUninstallSummary(string installDirectory)
{
    var plan = TimelineUninstallPlanForWindows(installDirectory);
    return string.Join(Environment.NewLine, plan);
}

static IEnumerable<string> TimelineUninstallPlanForWindows(string installDirectory)
{
    yield return "削除するもの";
    yield return " - Timeline の実行ファイル";
    yield return " - スタートメニューの起動項目";
    yield return " - Windows のアプリ一覧への登録";
    yield return "";
    yield return "残すもの";
    yield return " - settings.json";
    yield return " - data フォルダー";
    yield return " - logs フォルダー";
    yield return " - runtime フォルダー";
    yield return " - products フォルダー";
    yield return " - Docker のイメージ、ボリューム、ネットワーク";
    yield return "";
    yield return $"対象フォルダー: {installDirectory}";
}

static void WaitForParentProcess(int? parentPid)
{
    if (parentPid is null or <= 0)
    {
        return;
    }

    try
    {
        using var parent = Process.GetProcessById(parentPid.Value);
        parent.WaitForExit(30000);
    }
    catch (ArgumentException)
    {
    }
    catch (InvalidOperationException)
    {
    }
}

static void TryStopTimeline(string installDirectory, TimelineWindowsUninstallResult result)
{
    var launcher = Path.Combine(installDirectory, "launcher", "Timeline.Launcher.exe");
    if (!File.Exists(launcher))
    {
        result.Warnings.Add("Timeline launcher was not found. Runtime stop was skipped.");
        return;
    }

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = launcher,
            Arguments = "stop --root " + QuoteArgument(installDirectory),
            WorkingDirectory = Path.GetDirectoryName(launcher) ?? installDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            result.Warnings.Add("Timeline runtime stop could not be started.");
            return;
        }

        if (!process.WaitForExit(60000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            result.Warnings.Add("Timeline runtime stop timed out.");
            return;
        }

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            result.Warnings.Add($"Timeline runtime stop returned exit code {process.ExitCode}. {stderr}");
        }
        else
        {
            result.Messages.Add("Timeline runtime was stopped before uninstall.");
        }
    }
    catch (Exception ex)
    {
        result.Warnings.Add($"Timeline runtime stop was skipped. {ex.Message}");
    }
}

static void TryDeleteEmptyDirectory(string directory)
{
    try
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory, recursive: false);
        }
    }
    catch
    {
    }
}

static void WriteUninstallResult(TimelineWindowsUninstallResult result, string? explicitLogPath)
{
    var logPaths = new List<string>();
    if (!string.IsNullOrWhiteSpace(explicitLogPath))
    {
        logPaths.Add(Path.GetFullPath(explicitLogPath));
    }

    var runtimePath = Path.Combine(result.InstallDirectory, "runtime", "windows-uninstall-result.json");
    logPaths.Add(runtimePath);

    foreach (var path in logPaths.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? result.InstallDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions()) + Environment.NewLine);
        }
        catch
        {
        }
    }
}

static string QuoteArgument(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return "\"\"";
    }

    return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

static bool IsPathUnder(string path, string possibleParent)
{
    var fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
    var fullParent = EnsureTrailingSeparator(Path.GetFullPath(possibleParent));
    return fullPath.StartsWith(
        fullParent,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

static string EnsureTrailingSeparator(string path)
    => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
        ? path
        : path + Path.DirectorySeparatorChar;

static void WriteInstallReceipt(TimelineWindowsInstallResult result)
{
    var receiptDirectory = Path.Combine(result.InstallDirectory, "runtime");
    Directory.CreateDirectory(receiptDirectory);
    File.WriteAllText(
        Path.Combine(receiptDirectory, "windows-install-receipt.json"),
        JsonSerializer.Serialize(result, JsonOptions()) + Environment.NewLine);
}

static string ResolveArtifactPath(string? explicitArtifactPath)
{
    if (!string.IsNullOrWhiteSpace(explicitArtifactPath))
    {
        return Path.GetFullPath(explicitArtifactPath);
    }

    var currentDirectory = Directory.GetCurrentDirectory();
    var appDirectory = AppContext.BaseDirectory;
    var appParent = Directory.GetParent(appDirectory)?.FullName;
    var processDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? "");
    var processParent = string.IsNullOrWhiteSpace(processDirectory)
        ? null
        : Directory.GetParent(processDirectory)?.FullName;
    var candidates = new[] { currentDirectory, appDirectory, appParent, processDirectory, processParent }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .OfType<string>()
        .Select(Path.GetFullPath)
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .SelectMany(root => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "Timeline-win-*.zip", SearchOption.TopDirectoryOnly)
                .Concat(Directory.Exists(Path.Combine(root, "artifacts"))
                    ? Directory.EnumerateFiles(Path.Combine(root, "artifacts"), "Timeline-win-*.zip", SearchOption.TopDirectoryOnly)
                    : [])
            : [])
        .Where(path => !Path.GetFileName(path).Contains("-setup", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .ToArray();

    return candidates.Length == 1
        ? Path.GetFullPath(candidates[0])
        : throw new InvalidOperationException("Specify --artifact because a single Timeline Windows artifact could not be resolved.");
}

static string ResolveInstallDirectory(string? explicitInstallDirectory)
{
    if (!string.IsNullOrWhiteSpace(explicitInstallDirectory))
    {
        return Path.GetFullPath(explicitInstallDirectory);
    }

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(localAppData))
    {
        throw new InvalidOperationException("LocalApplicationData folder could not be resolved.");
    }

    return Path.Combine(localAppData, "Programs", "Timeline");
}

static void TryDeleteDirectory(string directory)
{
    try
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    catch
    {
    }
}

static void WriteResult(TimelineWindowsInstallResult result, bool jsonOutput)
{
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
        return;
    }

    Console.WriteLine($"Timeline Windows installer: {result.State}");
    Console.WriteLine($"  artifact: {result.ArtifactPath}");
    Console.WriteLine($"  install directory: {result.InstallDirectory}");
    if (!string.IsNullOrWhiteSpace(result.Version))
    {
        Console.WriteLine($"  version: {result.Version}");
    }

    foreach (var message in result.Messages)
    {
        Console.WriteLine($"  info: {message}");
    }

    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"  warning: {warning}");
    }

    foreach (var blocker in result.Blockers)
    {
        Console.WriteLine($"  blocker: {blocker}");
    }
}

static void WriteError(string message, bool jsonOutput)
{
    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { state = "failed", error = message }, JsonOptions()));
        return;
    }

    Console.Error.WriteLine(message);
}

static JsonSerializerOptions JsonOptions()
    => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

static void PrintHelp()
{
    Console.WriteLine("""
        Timeline Windows Installer

        Usage:
          Timeline.WindowsInstaller.exe --artifact <Timeline-win-x64-version.zip> [options]
          Timeline.WindowsInstaller.exe --uninstall [--install-dir <path>]

        Options:
          --artifact <path>      Timeline Windows product artifact ZIP.
          --install-dir <path>   Install directory. Defaults to %LOCALAPPDATA%\Programs\Timeline.
          --force                Replace existing application files while preserving user data.
          --wizard               Show the installer wizard.
          --uninstall            Show the uninstall wizard and remove application files after confirmation.
          --plan                 Show install plan only.
          --dry-run              Alias of --plan.
          --json                 Print machine-readable JSON.
          --help                 Show help.
        """);
}

internal sealed class TimelineWindowsInstallResult
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("artifactPath")]
    public string ArtifactPath { get; set; } = "";

    [JsonPropertyName("installDirectory")]
    public string InstallDirectory { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("runtimeIdentifier")]
    public string RuntimeIdentifier { get; set; } = "";

    [JsonPropertyName("force")]
    public bool Force { get; set; }

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<string> Blockers { get; set; } = [];

    [JsonPropertyName("shortcut")]
    public TimelineLauncherShortcutStatus? Shortcut { get; set; }

    [JsonPropertyName("uninstallRegistration")]
    public TimelineWindowsUninstallRegistrationStatus? UninstallRegistration { get; set; }
}

internal sealed class TimelineWindowsUninstallResult
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("installDirectory")]
    public string InstallDirectory { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("completedAt")]
    public string CompletedAt { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("shortcut")]
    public TimelineLauncherShortcutStatus? Shortcut { get; set; }

    [JsonPropertyName("uninstallRegistration")]
    public TimelineWindowsUninstallRegistrationStatus? UninstallRegistration { get; set; }
}

internal sealed record WindowsInstallerOptions(
    string? ArtifactPath,
    string? InstallDirectory,
    bool Force,
    bool PlanOnly,
    bool DryRun,
    bool JsonOutput,
    bool ShowHelp,
    bool Wizard,
    bool Uninstall,
    bool UninstallWorker,
    int? ParentPid,
    string? LogPath,
    bool ResultUi,
    bool SkipOsRegistration)
{
    public static WindowsInstallerOptions Parse(string[] args)
    {
        string? artifactPath = null;
        string? installDirectory = null;
        string? logPath = null;
        int? parentPid = null;
        var force = false;
        var planOnly = false;
        var dryRun = false;
        var jsonOutput = false;
        var showHelp = false;
        var wizard = false;
        var uninstall = false;
        var uninstallWorker = false;
        var resultUi = false;
        var skipOsRegistration = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (ReadValue(args, ref index, arg, "--artifact", ref artifactPath) ||
                ReadValue(args, ref index, arg, "--install-dir", ref installDirectory) ||
                ReadValue(args, ref index, arg, "--log", ref logPath))
            {
                continue;
            }

            if (ReadRequiredValue(args, ref index, arg, "--parent-pid", out var parentPidValue))
            {
                if (!int.TryParse(parentPidValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedParentPid) || parsedParentPid < 0)
                {
                    throw new ArgumentException("--parent-pid must be a non-negative integer.");
                }

                parentPid = parsedParentPid;
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--force":
                    force = true;
                    break;
                case "--wizard":
                    wizard = true;
                    break;
                case "--uninstall":
                    uninstall = true;
                    break;
                case "--uninstall-worker":
                    uninstallWorker = true;
                    break;
                case "--result-ui":
                    resultUi = true;
                    break;
                case "--skip-os-registration":
                    skipOsRegistration = true;
                    break;
                case "--plan":
                    planOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new WindowsInstallerOptions(
            artifactPath,
            installDirectory,
            force,
            planOnly,
            dryRun,
            jsonOutput,
            showHelp,
            wizard,
            uninstall,
            uninstallWorker,
            parentPid,
            logPath,
            resultUi,
            skipOsRegistration);
    }

    private static bool ReadValue(string[] args, ref int index, string arg, string optionName, ref string? value)
    {
        if (arg.StartsWith($"{optionName}=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(optionName.Length + 1)..];
            return true;
        }

        if (arg.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }

            value = args[++index];
            return true;
        }

        return false;
    }

    private static bool ReadRequiredValue(string[] args, ref int index, string arg, string optionName, out string value)
    {
        value = "";
        if (arg.StartsWith($"{optionName}=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(optionName.Length + 1)..];
            return true;
        }

        if (arg.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }

            value = args[++index];
            return true;
        }

        return false;
    }
}
