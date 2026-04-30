# Timeline

Timeline は、ローカルにある Timeline 系プロダクトを利用するための親UIです。

このリポジトリは `TimelineForAudio` などの変換エンジン本体を含みません。各プロダクトは `C:\apps` 配下の既存製品として扱い、Timeline はそれらの設定・確認・分析導線をまとめます。

## 現在の対象

- `C:\apps\TimelineForAudio`

現時点では TimelineForAudio の入力ディレクトリ、出力ディレクトリ、Hugging Face トークン、AI処理方法、音声ファイル一覧を扱います。

## 起動

Windows PowerShell で実行します。

```powershell
cd C:\apps\Timeline
.\start.ps1
```

Web UI:

```text
http://127.0.0.1:19000
```

停止:

```powershell
.\stop.ps1
```

## 構成

- `web/`: Blazor Web App
- `scripts/timeline-helper-server.ps1`: Windows側のローカル補助サーバー
- `docker-compose.yml`: Web UI の Docker 起動

Web は Docker 内で動きます。Windowsのディレクトリ選択や `C:\apps\TimelineForAudio` の設定ファイル操作は、ローカル補助サーバー経由で行います。

## 注意

入力ディレクトリを「対象から外す」操作では、元の音声ファイルや生成済みデータは削除されません。対象から外した後も、過去に生成されたrunやtimelineは出力ディレクトリに残ります。
