# Timeline ランチャー/ランタイム整理

この文書は、Timeline をユーザーから見て 1 つのアプリとして扱うためのランチャー責務を整理する。

## 基本方針

Timeline の通常導線は C# Launcher に集約する。

ユーザーに `bat`、`sh`、`.command` ファイルを実行させない。これらは OS ごとの差分や開発者向け都合が前面に出やすく、商品としての入口に向かないためである。

## ユーザー向け入口

| 入口 | 責務 | 実装 |
| --- | --- | --- |
| C# CLI Launcher | 起動、停止、状態確認、Webを開く | `launcher/Timeline.Launcher.csproj` |
| C# Resident Launcher | タスクバー/通知領域、macOS メニューバー相当で常駐する | `launcher-tray/Timeline.Launcher.Tray.csproj` |
| Windows Start Menu entry | Windows のアプリ入口として C# Resident Launcher を起動する | `TimelineLauncher shortcut-install` が `.lnk` を作成 |
| OS 自動起動設定 | OS 起動時に常駐 Launcher を起動する | Windows は Run レジストリ、macOS は LaunchAgent plist |

現時点ではインストーラーや署名済みアプリの完成前であるため、開発環境では `dotnet run --project ...` で起動する。配布時は同じ C# 実装を発行済み実行ファイルまたは DLL として起動する。

## 低レベル退避手段

`start.ps1` と `stop.ps1` は、移行期間中の開発者向け退避手段として残す。

ただし、通常の README、画面案内、OS 自動起動、ユーザー向け復旧導線では前面に出さない。ユーザーに理解させる対象は「Timeline Launcher」であり、PowerShell や Docker Compose の内部構造ではない。

## 削除した入口

以下は通常導線から外すため削除した。

| 旧入口 | 理由 |
| --- | --- |
| `TimelineLauncher.bat` | C# Launcher を呼ぶだけの wrapper であり、bat入口に見える |
| `TimelineStatus.bat` | 同上 |
| `TimelineStop.bat` | 同上 |
| `start.bat` / `stop.bat` | PowerShell 入口をユーザー向けに見せてしまう |
| `timeline-launcher.sh` | C# Launcher を呼ぶだけの shell wrapper であり、Mac/Linux 方針と矛盾する |
| `start.sh` / `stop.sh` | Mac/Linux の最小入口だったが、今後は C# Launcher と OS 固有登録へ寄せる |

## OS 自動起動

OS 起動時の自動起動は、C# 常駐 Launcher を直接起動する。

| OS | 登録方式 | shell wrapper |
| --- | --- | --- |
| Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 使わない |
| macOS | `~/Library/LaunchAgents/com.amanosystemlab.timeline.launcher.plist` | 使わない |
| Linux | 未対応 | 未定 |

Windows の旧 `Timeline Auto Start.cmd` が残っている場合は、設定保存時に削除して Run レジストリへ移行する。

## C# Launcher の責務

C# Launcher は次を担当する。

- Timeline Local API の発行と起動
- Docker Desktop / Docker Engine の状態確認
- Docker Compose による Timeline 本体の起動と停止
- Web / Local API / Worker / Ollama の状態確認
- Web が起動していない状態での最小診断
- 起動失敗時の次アクション提示

## Resident Launcher の責務

常駐 Launcher は、ユーザーの最初の入口になる。

- Open Timeline
- Start
- Stop
- Refresh Status
- Exit Launcher

現在は最小実装であり、通知、詳細ログ表示、アイコン、インストーラー連携は別タスクで扱う。

## Mac 対応の扱い

macOS でも同じ C# / Avalonia ベースの常駐 Launcher を使う方針とする。

この Windows 環境では、macOS のメニューバー表示、LaunchAgent の実機登録、署名、公証、Docker Desktop for Mac の挙動は確認できない。これらは Mac 対応 Epic 側で実機検証する。

## 完了判定

この整理での完了条件は次の通り。

- 通常導線が C# Launcher に寄っている
- OS 自動起動が shell wrapper を生成しない
- README が `bat` / `sh` / `.command` をユーザー向け入口として案内していない
- 旧 wrapper ファイルがリポジトリ直下に残っていない
- Mac 実機未検証の範囲が明示されている
