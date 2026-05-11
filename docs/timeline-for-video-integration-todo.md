# TimelineForVideo integration TODO

TimelineForVideo を Timeline 本体へ取り込むための作業リスト。

目的は、動画をサブ製品として認識し、スキャン、時間軸ストア、言語化、分析用ダウンロードへ段階的に接続すること。

## 現在の判断

- TimelineForVideo は既定では `<Timeline>\data\products\TimelineForVideo` に配置する。
- TimelineForVideo の README 上は `cli.ps1`、`items refresh`、`files list`、`items list`、`items download`、`models list` が存在する。
- 生成済み成果物は既定では `<Timeline>\data\to_text\video` に配置する。
- Timeline 本体のヘルパーからは、設定ファイルと既存成果物を読み取って概要を表示できる。
- 2026-05-09 時点では、`cli.ps1 settings status --json`、`models list --json`、`files list --page 1 --page-size 5 --json`、`items list --page 1 --page-size 5 --json`、`items download --json` が成功する。
- TimelineForVideo の `items download` は `--output` を持たないため、Timeline 側は製品が返す正式な ZIP パスを読み取って時間軸ストアへ取り込む。
- Timeline 側からの時系列再構築で Video 234 アイテム / 614,202 イベントを取り込めることを確認済み。

## TODO

- [x] TimelineForVideo の README、設定、既存成果物、CLI の現状を確認する。
- [x] 導入TODOを作成し、ブロッカーと実施順を明確にする。
- [x] Timeline 本体の製品レジストリへ `video` を追加する。
- [x] 製品管理モーダルで TimelineForVideo が表示されることを確認する。
- [x] Timeline 設定モーダルのプロモードで TimelineForVideo が表示されることを確認する。
- [x] Timeline 設定モーダルのプロモードに Video 固有設定を接続する。
- [x] 通常モードの入力データに「動画」を実装する。
- [x] PowerShell ASCII チェック、Docker build、Timeline 再起動、設定モーダル表示確認を行う。
- [x] TimelineForVideo の `README.md` と `AGENTS.md` を再確認し、最新の CLI 形を把握する。
- [x] TimelineForVideo を `cli.ps1` 経由で再確認し、主要コマンドが成功することを確認する。
- [x] Video の一覧ページを作るか、スキャン画面内の確認モーダルに統合するかを決める。スキャン画面から補助一覧へ遷移する独立ページとして実装済み。
- [x] Video の `items download` を Timeline 側から CLI 経由で呼ぶ実装を追加する。
- [x] Video の `timeline.json` を Timeline ストア形式へ変換する。
- [x] Video 内の `audio_acoustic_units` を音声言語化の対象に含める。
- [x] Video の OCR/text/visual/activity イベントを LLM 用代表表現へ変換する。
- [x] Timeline の全体ダウンロードへ Video を含める。
- [x] 既存4製品を壊していないことをビルドとAPIで確認する。
- [x] Video の大量イベントを、スキャン画面・分析用データ向けに読みやすい代表表現へ圧縮する。
- [x] 巨大な時系列ストアのソート方針を再設計する。

## CLI ブロッカー

TimelineForVideo の導入を完全に進めるには、少なくとも次が `cli.ps1` 経由で成功する必要がある。

```powershell
.\cli.ps1 settings status --json
.\cli.ps1 models list --json
.\cli.ps1 files list --page 1 --page-size 5 --json
.\cli.ps1 items list --page 1 --page-size 5 --json
.\cli.ps1 items download --json
```

Docker への直接操作やコンテナ内コマンド実行では確認しない。サブ製品の操作は必ず `cli.ps1` または既存ランチャー経由で行う。

## 2026-05-09 verification

- Timeline 本体の時系列再構築で `TimelineForVideo` の `items download --json` を実行し、成功した。
- 再構築結果: 4,415 アイテム / 750,841 イベント。
- Video 取り込み結果: 234 アイテム / 614,202 イベント。
- PC は製品として認識されるが、現在 production item が 0 件のため ZIP なしで skipped。
- Video の `audio_acoustic_units` は `phone_tokens` として保持し、通常の LLM 入力では読ませない。
- Video の先頭プレビューは `video_event_summary` の JSON 要約として返る。ユーザー向け・LLM向けには今後圧縮した代表表現が必要。
- `events.jsonl` が巨大化したため、200MB を超える場合はチャンク分割とマージによる外部ソートで時系列順を維持する。
