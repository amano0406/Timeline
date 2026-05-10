# Dashboard redesign TODO

ダッシュボードを、製品管理の入口ではなく、Timeline の状態を短時間で把握し、次の行動へ移るための画面として整理するための作業メモ。

## 採用方針

- ダッシュボードでは、製品管理カードを並べない。
- ユーザーが最初に知りたい `今日の状態`、`次のおすすめ`、`確認が必要なこと` を上から順に出す。
- データ量そのものは、Timeline が育っている実感を得る補助情報として扱う。
- 素材別の詳細確認や再処理は、ダッシュボードではなく `スキャン` と各一覧ページへ逃がす。
- 設定不足、製品未検出、古い時間軸、言語化待ちなど、次の行動に直結するものだけを強く出す。

## TODO

- [x] ダッシュボードから製品管理風のカード一覧を削除する。
- [x] `今日の状態` と `次のおすすめ` を追加する。
- [x] 設定不足、製品未検出、時間軸未作成、時間軸の古さ、言語化状態をアラート化する。
- [x] `時間軸イベント`、`取り込み済み素材`、`言語化待ち` の主要数値を表示する。
- [x] `Timeline の成長` として、製品別イベント数の横棒表示を追加する。
- [x] `素材別の状況` として、音声、動画、画像、ChatGPT、Windows Codex、PC状態を一覧する。
- [x] 言語化対象がある場合に `大きな問題なし` と見えないよう、状態文言を調整する。
- [x] 成長グラフの棒が Tailwind 生成状態に依存せず表示されるようにする。
- [x] 数値表記のカンマを揃える。
- [x] `dotnet.exe build web/Timeline.Web.csproj` を通す。
- [x] `scripts/check-powershell-ascii.ps1` を通す。
- [x] `scripts/smoke-web.ps1` を通す。
- [x] Playwright で PC 幅とスマホ幅のスクリーンショットを確認する。
- [x] ダッシュボード初期表示を、重い言語化ステータス取得で待たないようにする。
- [x] ダッシュボード初期表示を、サブ製品別の重い概要取得で待たないようにし、先に時間軸ストアと全体状態を表示する。
- [x] 詳細取得前の素材別カードが `未検出` や `未設定` に見えないよう、確認中の表示へ寄せる。

## 判断待ち・保留

- `Timeline の成長` は現時点では最新ストアの製品別イベント数であり、日別増加グラフではない。日別推移を見せるなら、スナップショット保存または履歴集計が必要。
- `スキャンから何日経過したか` をダッシュボードで出すには、製品別の最終スキャン日時・最終成功日時の扱いを統一する必要がある。
- `データが増える喜び` を出すなら、単なる総数ではなく、日次/週次の増加量、初回取り込みからの伸び、最近追加された素材の種類を使うとよい。
- 言語化品質が安定するまでは、ダッシュボードでは `品質検証モード` として控えめに表示する。

## 確認済み

- 2026-05-10 に `start.ps1 -NoOpen` で再起動し、TimelineForAudio / TimelineForWindowsCodex / TimelineForChatGPT / TimelineForImage / TimelineForVideo / TimelineForPC が ready、Web / Helper / Ollama が OK であることを確認。
- 2026-05-10 に smoke が通ることを確認。
- PC 幅スクリーンショット: `output/playwright/dashboard-desktop.png`
- スマホ幅スクリーンショット: `output/playwright/dashboard-mobile.png`
- 2026-05-10 に `/timeline/audio-verbalization/bulk/status` をダッシュボード初期表示から分離し、温まった状態では 5 秒時点でダッシュボード本体が表示されることを確認。
- 初期表示改善後のスクリーンショット: `output/playwright/verify-dashboard-rerun-5s.png`
- 2026-05-10 にダッシュボードの段階表示を再調整。2 秒時点で本体が表示され、詳細未取得の補助値は `確認中` として表示されることを確認。
- スクリーンショット: `output/playwright/dashboard-progressive-confirming-2s.png`
- スクリーンショット: `output/playwright/dashboard-progressive-after-fallback-8s.png`
