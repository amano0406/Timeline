# Timeline

Timeline は、ローカルにある Timeline 系プロダクトを利用するための親UIです。

このリポジトリは `TimelineForAudio` などの変換エンジン本体を含みません。各プロダクトは `C:\apps` 配下の既存製品として扱い、Timeline はそれらの設定・確認・分析導線をまとめます。

## 現在の対象

Timeline は次のローカル製品を扱います。各製品の操作は、その製品の `cli.ps1` 経由で行います。

- `C:\apps\TimelineForAudio`
- `C:\apps\TimelineForWindowsCodex`
- `C:\apps\TimelineForChatGPT`
- `C:\apps\TimelineForImage`

Timeline は各製品の Docker コンテナへ直接入って処理を実行しません。各製品が生成した成果物ディレクトリを読み取ることはありますが、製品操作は `cli.ps1` を正面玄関にします。

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

## タイムラインストア

Timeline 自体は、各製品の成果物を時間軸で扱うためのストアを持ちます。

既定の場所:

```text
C:\TimelineData\Timeline\store
C:\TimelineData\Timeline\work
```

ストアの主なファイル:

```text
store\manifest.json
store\items.jsonl
store\events.jsonl
store\rebuilds\<rebuild-id>\
```

画面の「タイムライン」では、製品横断の時間軸を再構築して一覧表示します。再構築時は、各製品から `cli.ps1` 経由でローカルの Timeline 作業ディレクトリへダウンロードし、その後 Timeline 側で `items.jsonl` と `events.jsonl` に正規化します。

ストアの ZIP ダウンロードは、再構築済みのストアを ZIP 化します。ダウンロード時に各製品から勝手に再収集する設計ではありません。

## 動作確認

Timeline 起動後、Web ルートと各サブ製品の `cli.ps1` 契約を確認できます。

```powershell
.\scripts\check-powershell-ascii.ps1
.\scripts\smoke-web.ps1
.\scripts\check-product-cli-contracts.ps1
```

ダウンロード作成まで含めて確認する場合は、次を実行します。失敗した場合は、Timeline 側のフォールバックではなく、対象製品の `cli.ps1` 契約または保存先パス解釈を修正します。

```powershell
.\scripts\check-product-cli-contracts.ps1 -IncludeDownloads
```

TimelineForAudio のダウンロード導線だけを集中的に確認する場合は、次も使えます。

```powershell
.\scripts\smoke-audio-ps1-download.ps1
```

## PowerShell の文字コードガード

Timeline は Windows PowerShell 5.1 の起動導線を使います。`.ps1` に日本語などの非 ASCII 文字を入れると、UTF-8/BOM なしファイルを Windows PowerShell 5.1 が誤読し、構文が壊れることがあります。

そのため、`.ps1` は原則 ASCII のみにしてください。ユーザー向けの日本語文言は Blazor/C# 側、または JSON などのリソース側に置きます。

`start.ps1` と `stop.ps1` は、補助スクリプトを読み込む前に `scripts\check-powershell-ascii.ps1` を実行します。非 ASCII 文字が混入している場合は、起動前に検知して停止します。

## 構成

- `web/`: Blazor Web App
- `scripts/timeline-helper-server.ps1`: Windows側のローカル補助サーバー
- `scripts/timeline-store-worker.ps1`: Timeline ストア再構築用の Windows 側ワーカー
- `worker/`: Timeline 所有の Docker worker。現在はストア監視と heartbeat を担当
- `docker-compose.yml`: Web UI と Timeline worker の Docker 起動

Web は Docker 内で動きます。Windows のディレクトリ選択や `C:\apps` 配下の各製品 CLI 操作は、ローカル補助サーバー経由で行います。

Timeline worker は Timeline の所有物です。サブ製品の Docker を直接操作するための層ではありません。サブ製品の収集が必要な処理は Windows 側 worker から各製品の `cli.ps1` を呼びます。

## 注意

入力ディレクトリを「対象から外す」操作では、元の音声ファイルや生成済みデータは削除されません。対象から外した後も、過去に生成されたrunやtimelineは出力ディレクトリに残ります。
