# Timeline

Timeline は、ローカルにある Timeline 系サブ製品を扱うための親 UI、製品管理画面、製品横断の時間軸ストアを持つアプリです。

このリポジトリは `TimelineForAudio` などの変換エンジン本体を含みません。各サブ製品は独立したローカル製品として扱い、それぞれの `cli.ps1`、設定、worker、生成データを持ちます。Timeline はそれらを公開された Windows 側の入口から操作し、サブ製品の出力を Timeline 側のデータとしてスキャン、確認、ダウンロード、LLM 分析向けに整えます。

Timeline はサブ製品が生成済みのファイルを読むことはあります。ただし、サブ製品の Docker コンテナへ直接入って処理を実行してはいけません。

## 対応サブ製品

既定の製品レジストリは、`C:\apps` 配下の次の製品を参照します。

- `C:\apps\TimelineForAudio`
- `C:\apps\TimelineForVideo`
- `C:\apps\TimelineForImage`
- `C:\apps\TimelineForWindowsCodex`
- `C:\apps\TimelineForChatGPT`
- `C:\apps\TimelineForPC`

各製品は `cli.ps1` を公開入口として持つ前提です。新しい製品は `timeline-product.json` も持つことで、Timeline 側が製品 ID、リポジトリ、リリース ZIP、起動方式、アンインストール方針などを固定実装なしで理解できるようにします。

マニフェスト設計:

```text
docs\timeline-product-manifest.md
```

サブ製品管理設計:

```text
docs\sub-product-management-design.html
docs\product-uninstall-design.md
```

方針メモ:

```text
docs\future-product-roadmap.html
docs\monetization-and-product-strategy-notes.html
docs\timeline-llm-data-rules.html
```

## 現在の役割

Timeline の実用上の役割は大きく4つです。

1. 必要な設定、製品、バックグラウンドサービスが揃っているかを見せる。
2. 各サブ製品の素材をスキャンし、取り込み状況を確認できるようにする。
3. 各サブ製品の出力から Timeline 自体の時間軸ストアを再構築する。
4. サブ製品のインストール、アンインストール、起動、停止、再起動、状態確認を行う。

サブ製品の一覧ページは、深い分析画面ではなく「取り込み確認」「処理結果確認」のための画面です。音声、動画、画像、スレッド、PC 状態などが検出・変換できているかを確認することに寄せています。

一覧ページは通常のページングを使います。無限スクロールや仮想スクロールは、検索、日付範囲、フィルタ、時間軸分析の導線が固まった後に再検討します。

## 起動

PowerShell で実行します。

```powershell
cd C:\apps\Timeline
.\start.ps1
```

開発中など、ブラウザーのタブを増やしたくない場合:

```powershell
.\start.ps1 -NoOpen
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
  - 重要な警告、次に確認すべきこと、Timeline の成長、素材別の状態を表示します。
  - 低レベルな製品操作ではなく、ユーザーが次の行動を決めるための画面です。
- スキャン
  - Timeline データを最新状態に近づけるための中心画面です。
  - 「スキャン」操作で、各製品の CLI からの取得、Timeline ストア再構築、必要なデータ整備、対象となる音声・動画の言語化をまとめて実行します。
  - 素材カードから、音声、動画、画像、Windows Codex、ChatGPT、PC 状態の確認一覧へ進みます。
- 設定モーダル
  - 通常モード: 表示言語、タイムゾーン、Timeline データ保存先、共通 AI 設定、Hugging Face トークン、入力ディレクトリ。
  - 詳細モード: Timeline 本体と各サブ製品の `settings.json` に近い細かい設定。
- 製品管理モーダル
  - 導入済み・未導入の状態、配置先、起動状態、操作できる内容を表示します。
  - インストール、アンインストール、起動、停止、再起動、状態更新を扱います。
- 音声ファイル
  - ファイルツリー、ページング、生成データ削除、ダウンロード、音声詳細、再生、言語化状態。
- 動画ファイル
  - ファイル一覧、詳細、対応している生成データのダウンロード・削除、言語化状態。
- 画像ファイル
  - ファイル一覧、詳細、対応している生成データのダウンロード・削除。
- Windows Codex
  - スレッド一覧、スレッド詳細、Markdown 表示、対応している生成データのダウンロード・削除。
- ChatGPT
  - 必要に応じた ZIP アップロード、スレッド一覧、スレッド詳細、Markdown 表示、対応している生成データのダウンロード・削除。
- PC 状態
  - TimelineForPC の項目一覧。詳細画面は、必要になった時点で専用 UI を追加します。

各一覧ページは共通のページング UI を使います。

```text
1 - 100 / 41,596 件
1 / 416 ページ
最初 / 前へ / 1 2 3 4 5 / 次へ / 最後
```

チェックボックスによる選択操作は、基本的に現在表示中のページが対象です。全件操作は「すべてダウンロード」のようにボタン文言で明示します。

## タイムラインストア

Timeline 自体は、各製品の成果物を時間軸で扱うためのストアを持ちます。

既定のルート:

```text
C:\TimelineData\Timeline
```

Timeline は、その配下で次のディレクトリを管理します。

```text
C:\TimelineData\Timeline\work
C:\TimelineData\Timeline\store
C:\TimelineData\Timeline\logs
```

ストアの主なファイル:

```text
store\manifest.json
store\items.jsonl
store\events.jsonl
store\rebuilds\<rebuild-id>\
```

スキャン画面では、各サブ製品の `cli.ps1` を通して Timeline の作業ディレクトリへデータを取得し、その後 Timeline 側で `items.jsonl` と `events.jsonl` に正規化します。

Timeline ZIP ダウンロードは、再構築済みの Timeline ストアを ZIP 化します。ダウンロード時に各サブ製品から勝手に再収集する設計ではありません。

Timeline では、データを大きく3層に分けます。

1. Timeline マスターデータ: 事実、元データ参照、変換経緯。
2. LLM 入力データ: 分析、レポート、検索回答、記事生成に使うための読みやすいテキスト中心データ。
3. LLM 生成結果: レポート、分析文、仮説、次の行動などの派生データ。

音声・動画のフォントークン、OCR 前の画像、動画フレーム、バイナリファイルのような直接読みにくい中間データは、Timeline マスターまたは raw 参照として保持します。通常の LLM 入力には、言語化済み音声、OCR テキスト、画像説明、スレッド本文、操作要約など、読みやすい表現を使います。

この分離ルールは次にまとめています。

```text
docs\timeline-llm-data-rules.html
```

## 音声・動画の言語化

TimelineForAudio と TimelineForVideo が出力するフォントークンの時間軸は、Timeline 本体で読みやすい候補文に変換します。前後の Timeline 情報や前チャンクの結果を弱いヒントとして使うため、この責務はサブ製品ではなく Timeline 側に置きます。

この機能はまだ品質調整中です。実装としてはチャンク処理やキュー処理に対応していますが、出力品質が製品レベルに届くまでは、意図的に処理対象を絞る場合があります。

現在の実装:

- 発話区間から 5〜10分程度のチャンク計画を作成
- Timeline ストア配下にチャンクごとの `context/*.context.json` と `summary.json` を作成
- 長時間処理を PowerShell worker に渡し、Web リクエストはすぐ返す
- Timeline 所有の Ollama Docker サービスへ JSON 返却指定で送信
- 近い時間の Timeline テキスト候補や前チャンクの結果を弱いヒントとして利用
- 完了または失敗した結果を Timeline ストア配下に保存
- Ollama URL、モデル、チャンク幅、同時実行数は Timeline 内部で管理

既定モデル:

```text
qwen3.5:9b
```

`start.ps1` は `docker-compose.yml` 経由で Ollama を起動し、初回起動時に既定モデルを取得します。モデルデータは Docker volume の `ollama` に保存します。Timeline では Ollama を localhost のみに公開します。

```text
http://127.0.0.1:11434
```

## 操作ログ

Timeline は、インシデント確認用の永続操作ログを次に保存します。

```text
C:\TimelineData\Timeline\logs\operations\<operation-id>\
```

各操作ディレクトリには次が入ります。

```text
events.jsonl
summary.json
```

Web 操作は親操作として記録されます。その Web 操作中に起動された CLI 呼び出しは `parentOperationId` を持つ子操作として記録されるため、ボタン/API 操作から、対象製品の `cli.ps1`、終了コード、stdout/stderr の末尾、worker 状態変化まで追跡できます。

これらのログは内部診断用です。ユーザー向け UI は、常時表示のコンソールに依存しない方針です。

Web 操作の確認チェックリスト:

```text
docs\operation-log-web-test-checklist.md
```

## サブ製品操作ルール

Timeline がサブ製品を操作する場合は、その製品の公開された Windows 側入口を使います。

許可すること:

- Windows 側の補助サーバーまたは worker から、対象製品の `cli.ps1` を実行する
- `scripts\invoke-product-cli-utf8.ps1` 経由で PowerShell の JSON 出力を UTF-8 として安全に扱う
- 表示や ZIP 化のために、サブ製品が生成済みの成果物ファイルを読む
- このリポジトリが所有する Timeline の Docker サービスを操作する
- 製品が対応している場合、その製品の `start.ps1` と `stop.ps1` を使って起動・停止する

禁止すること:

- サブ製品の Docker コンテナに入る
- サブ製品の Docker コンテナ内でコマンド、Python、shell を実行する
- `cli.ps1` のダウンロード処理が失敗したときに、勝手に成果物ディレクトリを読んで処理を続ける
- 他プロダクトのアプリケーションディレクトリをダウンロード先や作業場所として使う

Timeline 管理のアンインストールは、Timeline の製品レジストリとアンインストール計画に基づいて行います。サブ製品の `uninstall.ps1` は呼びません。これは製品単体利用者向けの入口として扱います。既定の削除対象は、設定されている製品アプリケーションディレクトリです。マスターデータや生成データは、アンインストール画面の選択肢に基づいて扱います。

## 製品ソース ZIP

Timeline とサブ製品は、公開 GitHub タグから自動生成される source archive を配布の基本形にします。公開リポジトリであれば、通常ユーザーは Git も GitHub アカウントも不要です。

例:

```text
https://github.com/amano0406/TimelineForAudio/archive/refs/tags/v0.4.7.zip
```

Timeline の製品設定には GitHub リポジトリ URL を保存し、インストールまたは更新時に最新タグを確認して対応する source archive ZIP を取得します。

source archive には、ローカル実行に必要な製品ファイルを含めます。ローカル設定、生成データ、Docker volume、キャッシュ、ビルド出力はリポジトリにコミットしないため、GitHub の source archive にも含まれません。

配布ルールは次にまとめています。

```text
docs\timeline-product-manifest.md
```

## 動作確認

Timeline 起動後、Web ルートと各サブ製品の `cli.ps1` 契約を確認できます。

```powershell
.\scripts\check-powershell-ascii.ps1
.\scripts\smoke-web.ps1
.\scripts\check-product-cli-contracts.ps1
```

ダウンロード作成まで含めて確認する場合:

```powershell
.\scripts\check-product-cli-contracts.ps1 -IncludeDownloads
```

失敗した場合は、Timeline 側のフォールバックではなく、対象製品の `cli.ps1` 契約または保存先パス解釈を修正します。

TimelineForAudio のダウンロード導線だけを集中的に確認する場合:

```powershell
.\scripts\smoke-audio-ps1-download.ps1
```

アンインストール挙動を確認する場合は、テスト用製品またはバックアップ済みの製品ディレクトリを使ってから実行します。

```powershell
.\scripts\test-product-uninstall.ps1
```

## 構成

- `web/`: Blazor Web App
- `scripts\timeline-helper-server.ps1`: Windows 側のローカル補助サーバー
- `scripts\timeline-store-worker.ps1`: Timeline ストア再構築用の Windows 側 worker
- `scripts\audio-verbalization-worker.ps1`: 音声・動画言語化 worker
- `scripts\audio-verbalization-bulk-worker.ps1`: 言語化キュー worker
- `worker/`: Timeline 所有の Docker worker。ストア監視と heartbeat を担当
- `docker-compose.yml`: Web UI、Timeline worker、Ollama の Docker 起動

Timeline worker は Timeline の所有物です。サブ製品の Docker を直接操作するための層ではありません。

## レスポンシブ UI

PC とスマートフォン幅の両方に対応しています。

- PC は左サイドバー固定
- スマートフォン幅では上部バーと開閉式サイドバー
- 一覧はカード内スクロールにして、ページ全体が長くなりすぎないように制御
- 一覧テーブルのヘッダーは、カード内スクロール中も見えるように固定

## PowerShell の文字コードガード

Timeline は Windows PowerShell 5.1 の起動導線を使います。`.ps1` に日本語などの非 ASCII 文字を入れると、UTF-8/BOM なしファイルを Windows PowerShell 5.1 が誤読し、構文が壊れることがあります。

そのため、`.ps1` は原則 ASCII のみにしてください。ユーザー向けの日本語文言は Blazor/C# 側、または JSON などのリソース側に置きます。

`start.ps1` と `stop.ps1` は、補助スクリプトを読み込む前に `scripts\check-powershell-ascii.ps1` を実行します。非 ASCII 文字が混入している場合は、起動前に検知して停止します。
