# Timeline

Timeline は、ローカルにある Timeline 系プロダクトを利用するための親UIであり、製品横断の時間軸ストアを持つアプリです。

このリポジトリは `TimelineForAudio` などの変換エンジン本体を含みません。各プロダクトは `C:\apps` 配下の既存製品として扱い、Timeline はそれらの設定・確認・分析導線をまとめます。

## 現在の対象

Timeline は次のローカル製品を扱います。各製品の操作は、その製品の `cli.ps1` 経由で行います。

- `C:\apps\TimelineForAudio`
- `C:\apps\TimelineForWindowsCodex`
- `C:\apps\TimelineForChatGPT`
- `C:\apps\TimelineForImage`

Timeline は各製品の Docker コンテナへ直接入って処理を実行しません。各製品が生成した成果物ディレクトリを読み取ることはありますが、製品操作は `cli.ps1` を正面玄関にします。

## 現在の役割

Timeline の役割は大きく2つです。

1. 各サブ製品が正しく設定され、起動し、取り込み・生成できているかをローカルUIで確認する。
2. 各サブ製品の出力を `cli.ps1` 経由で収集し、Timeline 自体の時間軸ストアとして正規化する。

サブ製品の一覧ページは、深い探索画面ではなく「取り込み確認」「処理結果確認」のための画面です。そのため、無限スクロールや自動全件読み込みではなく、通常のページングを使います。

メインの「時間軸」ページも、現時点では通常のページングです。日付範囲、検索、プロダクト種別フィルタなどが充実した後に、検索結果の見せ方として無限スクロールや仮想スクロールを再検討します。

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

ダブルクリック用の入口として、`start.bat` と `stop.bat` もあります。

## 画面

- ダッシュボード
  - 各プロダクトの起動状態、開く、設定、起動、再起動
- 時間軸
  - 製品横断の Timeline ストア再構築、一覧表示、ZIP ダウンロード
- 基本設定
  - 表示言語、タイムゾーン、作業ディレクトリ、時間軸ストア
- TimelineForAudio
  - 音声ファイル一覧、分析状態、生成物削除、ダウンロード、音声詳細、設定
- TimelineForWindowsCodex
  - スレッド一覧、スレッド詳細、生成物削除、ダウンロード、設定
- TimelineForChatGPT
  - ZIPアップロード、スレッド一覧、スレッド詳細、生成物削除、ダウンロード、設定
- TimelineForImage
  - 画像ファイル一覧、再スキャンして分析、生成物削除、ダウンロード、設定

各一覧ページは共通のページングUIを使います。

```text
1 - 100 / 41,596 件
1 / 416 ページ
最初 / 前へ / 1 2 3 4 5 / 次へ / 最後
```

チェックボックスによる選択操作は、基本的に現在表示中のページが対象です。全件に対する操作は、「すべてダウンロード」のようにボタン文言で明示します。

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

基本設定では、次を管理します。

- 表示言語
- タイムゾーン
- Timeline 作業ディレクトリ
- Timeline ストアディレクトリ

作業ディレクトリやストアディレクトリを変更する場合は、Docker worker からも読めるように `docker-compose.yml` の bind mount と整合させてください。

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

## サブ製品操作ルール

Timeline がサブ製品を操作する場合は、その製品の公開入口である `cli.ps1` を使います。

許可すること:

- Windows側の補助サーバーまたは worker から、対象製品の `cli.ps1` を実行する
- 表示やZIP化のために、サブ製品が生成済みの成果物ファイルを読む
- このリポジトリが所有する Timeline の Docker サービスを操作する

禁止すること:

- サブ製品の Docker コンテナに入る
- サブ製品の Docker コンテナ内でコマンド、Python、shell を実行する
- `cli.ps1` のダウンロード処理が失敗したときに、勝手に成果物ディレクトリを読んで処理を続ける
- 他プロダクトのアプリケーションディレクトリをダウンロード先や作業場所として使う

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

## 音声の言語化

TimelineForAudio が出力するフォントークンの時間軸は、Timeline 本体で読みやすい候補文に変換します。前後の時間軸情報や前チャンクの結果を弱いヒントとして使うため、この責務は TimelineForAudio ではなく Timeline 側に置きます。

現在の実装:

- 音声タイムラインの発話区間から 5〜10分程度のチャンク計画を作成
- Timeline ストア配下にチャンクごとの `context/*.context.json` と `summary.json` を作成
- Timeline 所有の Ollama Docker サービスの `/api/chat` に JSON 返却指定で送信
- 完了または失敗した結果を `store\audio-verbalizations\<audio-item-id>\audio-verbalization.json` に保存
- Timeline 設定画面から Ollama の接続確認が可能

既定モデル:

```text
qwen3.5:9b
```

`start.ps1` は `docker-compose.yml` 経由で Ollama を起動し、初回起動時に既定モデルを取得します。モデルデータは Docker volume の `ollama` に保存します。Timeline では Ollama を localhost のみに公開します。

```text
http://127.0.0.1:11434
```

## レスポンシブUI

PC とスマートフォン幅の両方に対応しています。

- PC は左サイドバー固定
- スマートフォン幅では上部バーと開閉式サイドバー
- サイドバーを開いている間は背景側のスクロールを抑制
- 一覧はカード内スクロールにして、ページ全体が長くなりすぎないように制御
- 一覧テーブルのヘッダーは、カード内スクロール中も見えるように固定

## 注意

入力ディレクトリを「対象から外す」操作では、元の音声ファイルや生成済みデータは削除されません。対象から外した後も、過去に生成されたrunやtimelineは出力ディレクトリに残ります。
