# ディレクトリルート整理 TODO

目的: `C:\apps` や `C:\TimelineData` を Timeline の固定前提にせず、Timeline 本体の `settings.json` とデータルートから管理対象ディレクトリを導出する。

## 確定ルール

- [x] Timeline 本体の `settings.json` は Timeline 製品ディレクトリ直下に置く。
- [x] `dataRoot` の既定値は `data` とする。
- [x] `dataRoot` が空または相対パスの場合、Timeline 製品ディレクトリ基準で解決する。
- [x] `dataRoot` がドライブ付きパスまたは UNC パスの場合、絶対パスとして扱う。
- [x] 旧 `store` は `to_timeline` に置き換える。

## 既定ディレクトリ

- [x] `data\products\<sub-product>`: Timeline 管理のサブ製品配置先。
- [x] `data\work`: Timeline の一時作業場所。
- [x] `data\to_text\<sub-product>`: サブ製品が生成した読み取り用データの受け皿。
- [x] `data\to_timeline`: Timeline 形式に正規化した時間軸データ。
- [x] `data\logs`: 操作ログと診断ログ。
- [x] `data\backups`: 設定退避、アンインストール時の退避先。
- [x] `data\test`: ローカル動作確認用データ。

## 実装チェック

- [x] Web モデルに `dataRoot` / `resolvedDataRoot` を追加する。
- [x] 設定保存 API で `workDirectory` / `storeDirectory` ではなく `dataRoot` を保存する。
- [x] 旧 `workDirectory` / `storeDirectory` から `dataRoot` を推定できるようにする。
- [x] helper server の既定サブ製品配置を `data\products` 派生にする。
- [x] helper server の作業先、時間軸先、ログ先、退避先を `dataRoot` 派生にする。
- [x] サブ製品の既定出力先を `data\to_text\<product>` に寄せる。
- [x] `start.ps1` で `dataRoot` を解決し、Docker bind source を環境変数で渡す。
- [x] `docker-compose.yml` のホスト側 bind source を固定 Windows パスから外す。
- [x] worker スクリプトの Timeline 本体パス既定値を固定 Windows パスから外す。
- [x] 設定画面の保存先表示を `dataRoot` 前提にする。
- [x] テスト・smoke スクリプトの既定ディレクトリを Timeline 製品配下にする。
- [x] README / README.ja を新しいディレクトリ構成に更新する。
- [x] 現行 docs / README / scripts / web の固定 `C:\apps` / `C:\TimelineData` 参照を棚卸しし、履歴メモ以外を更新する。

## 確認チェック

- [x] PowerShell ファイルの ASCII チェックを通す。
- [x] PowerShell helper の構文確認を通す。
- [x] Web / worker のビルドを通す。
- [ ] 実画面で設定ページが `dataRoot` を表示・保存できることを確認する。
- [ ] `start.ps1 -NoOpen` で `data\work` と `data\to_timeline` が Docker に渡ることを確認する。

## 保留

- [ ] 既存ユーザーの旧 `C:\TimelineData\Timeline\store` 実データを自動移行するかどうかは未確定。
- [ ] `dataRoot` 変更時に既存データをコピーするか、空の新ルートとして扱うかは未確定。
- [ ] データベース導入時に `to_timeline` 配下のファイル構成をどう置き換えるかは未確定。
