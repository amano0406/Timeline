# Timeline サブ製品マニフェスト仕様

## 目的

Timeline は複数のサブ製品を扱います。

例:

- TimelineForAudio
- TimelineForImage
- TimelineForVideo
- TimelineForWindowsCodex
- TimelineForChatGPT
- TimelineForPC

これらを Timeline 側が推測で扱うと、以下の問題が起きます。

- 起動スクリプト名を推測してしまう
- CLI の場所を推測してしまう
- 設定ファイルの場所を推測してしまう
- 生成データの場所を推測してしまう
- アンインストール時に削除してよい範囲を推測してしまう
- Docker 関連リソースを推測で削除しそうになる

そのため、各サブ製品は Timeline から読める共通形式のマニフェストを持ちます。

## ファイル名

サブ製品のルート直下に、次のファイルを置きます。

```text
timeline-product.json
```

例:

```text
C:\apps\TimelineForAudio\timeline-product.json
C:\apps\TimelineForImage\timeline-product.json
C:\apps\TimelineForVideo\timeline-product.json
```

`manifest.json` という名前は使いません。

理由:

- Timeline 内部にも既存の `manifest.json` がある
- 何のマニフェストなのか曖昧になる
- サブ製品管理用であることをファイル名から分かるようにしたい

## 責務分担

### Timeline 側が持つもの

Timeline 側は、インストール前に必要な最低限の製品カタログを持ちます。

主な情報:

- 製品 ID
- 表示名
- 入手方式
- 入手元 URL
- インストール先
- 有効 / 無効

例:

```json
{
  "productRegistry": {
    "products": [
      {
        "id": "audio",
        "displayName": "TimelineForAudio",
        "path": "C:\\apps\\TimelineForAudio",
        "sourceType": "github-source-archive",
        "sourceUrl": "https://github.com/amano0406/TimelineForAudio",
        "version": "",
        "enabled": true,
        "required": false
      }
    ]
  }
}
```

これは、まだサブ製品が存在しない状態でも必要です。
インストール前はサブ製品内の `timeline-product.json` を読めないためです。

### サブ製品側が持つもの

サブ製品側は、自分自身を Timeline が安全に扱うための情報を `timeline-product.json` に書きます。

主な情報:

- 製品 ID
- 表示名
- 製品種別
- 起動 / 停止 / CLI の場所
- 設定ファイルの場所
- 対応機能
- 入力データ設定の場所
- 生成データ設定の場所
- アンインストール時の扱い
- Docker 関連を Timeline が管理してよいか

Timeline はこのファイルを読み、製品ごとの推測コードを減らします。

## 通常設定と個別上書き設定

設定画面の概念は、従来の「通常モード / プロモード」よりも、次の名前の方が実態に近いです。

| 旧名称 | 新しい考え方 | 役割 |
|---|---|---|
| 通常モード | 基本設定 | Timeline 全体のデフォルトを決める |
| プロモード | 個別上書き設定 | 製品ごとの差分だけを上書きする |

基本設定は、全体の既定値です。

例:

- データ保存先
- 表示言語
- タイムゾーン
- 共通の Hugging Face トークン
- 共通の AI 処理方法

個別上書き設定は、製品ごとの例外です。

例:

- TimelineForVideo だけ別の入力ディレクトリを使う
- TimelineForAudio だけ別の出力先を使う
- 特定製品だけ CPU 処理にする
- 特定製品だけ別のトークンを使う

基本設定を変更しても、個別上書き設定は勝手に消しません。
個別上書きがある項目は、個別上書きが優先されます。

## インストール方式と Git 依存

### Git clone

公開リポジトリを `git clone` するだけなら、通常 GitHub アカウントは不要です。

必要なもの:

- Git
- ネットワーク接続

不要なもの:

- GitHub アカウント
- GitHub トークン

ただし、非公開リポジトリの場合は GitHub アカウントや認証が必要です。

### GitHub source archive

公開 GitHub リポジトリのタグから自動生成される source archive ZIP をダウンロードして展開する方式なら、通常 Git も GitHub アカウントも不要です。

必要なもの:

- ネットワーク接続

不要なもの:

- Git
- GitHub アカウント
- GitHub トークン

一般ユーザー向けの正式インストール方式は、GitHub の source archive ZIP を優先します。
Git clone は、開発者向けまたは検証向けとして扱います。

## 配布 ZIP のルール

Timeline 本体とサブ製品は、独自配布 ZIP を作らず、GitHub がタグから自動生成する source archive ZIP を基本にします。

URL 例:

```text
https://github.com/amano0406/TimelineForAudio/archive/refs/tags/v0.4.7.zip
https://github.com/amano0406/TimelineForImage/archive/refs/tags/v0.2.5.zip
https://github.com/amano0406/Timeline/archive/refs/tags/v0.2.2.zip
```

採用理由:

- 一般ユーザーに Git や GitHub アカウントを要求しない
- 独自 ZIP 生成スクリプトを各リポジトリで保守しなくてよい
- Timeline 側は `sourceUrl` に GitHub リポジトリ URL を持ち、最新タグから source archive ZIP URL を解決する
- 最新タグの取得は公開タグフィードを優先し、必要な場合だけ GitHub API にフォールバックする
- Git タグを正本にすることで、ZIP の中身とリリースしたコミットの対応が明確になる
- `settings.json`、生成データ、Docker 作業データなど、Git 管理されていないローカル情報を混ぜない
- GitHub Release の source code ZIP/TAR が自動で用意される

将来、チェックサム、署名、ビルド済みバイナリ、ライセンス別配布などが必要になった場合だけ、Release asset として独自 ZIP を再検討します。

## 現時点の Git 依存箇所

Timeline 本体で Git が必要になる箇所は、現時点では限定的です。

| 用途 | Git 必須か | 備考 |
|---|---:|---|
| サブ製品を Git clone でインストールする | 必須 | source archive ZIP 方式にすれば不要 |
| `.git` がある製品のアンインストール前に作業ツリーを確認する | 条件付き | `.git` がなければ確認しない |
| Timeline 本体の通常利用 | 不要 | Git は不要 |
| サブ製品の通常起動 / 停止 / CLI 実行 | 不要 | `start.ps1` / `stop.ps1` / `cli.ps1` を使う |

つまり、一般ユーザー向けに Git を必須にしないためには、インストール方式を GitHub の source archive ZIP に寄せます。

## `timeline-product.json` の例

```json
{
  "schemaVersion": 1,
  "productId": "audio",
  "displayName": "TimelineForAudio",
  "productKind": "media-audio",
  "description": "Audio files are converted into speaker-attributed timeline data.",
  "commands": {
    "start": {
      "type": "powershell",
      "path": "start.ps1"
    },
    "stop": {
      "type": "powershell",
      "path": "stop.ps1"
    },
    "cli": {
      "type": "powershell",
      "path": "cli.ps1"
    }
  },
  "settings": {
    "file": "settings.json",
    "supportsBasicDefaults": true,
    "supportsProductOverrides": true
  },
  "capabilities": {
    "fileList": true,
    "itemList": true,
    "itemRefresh": true,
    "itemDownload": true,
    "itemRemove": true,
    "modelList": true,
    "verbalization": true
  },
  "data": {
    "sourcePathsFromSettings": [
      "inputRoots[].path",
      "inputDirectories[].path"
    ],
    "generatedPathsFromSettings": [
      "outputRoots[].path",
      "masterRoot.path",
      "outputRoot.path"
    ],
    "deleteSourceData": false
  },
  "uninstall": {
    "deleteAppDirectory": true,
    "backupSettingsByDefault": true,
    "deleteGeneratedDataOptional": true,
    "deleteGeneratedDataByDefault": false,
    "deleteSourceData": false
  },
  "runtime": {
    "usesDocker": true,
    "dockerManagedByTimeline": false
  }
}
```

## フィールド仕様

### schemaVersion

マニフェスト形式のバージョンです。

初期値:

```json
1
```

### productId

Timeline 側で使う製品 ID です。

例:

```json
"audio"
```

推奨 ID:

| 製品 | productId |
|---|---|
| TimelineForAudio | audio |
| TimelineForImage | image |
| TimelineForVideo | video |
| TimelineForWindowsCodex | windows-codex |
| TimelineForChatGPT | chatgpt |
| TimelineForPC | pc |

### displayName

画面表示名です。

例:

```json
"TimelineForAudio"
```

### productKind

製品の種類です。

例:

```json
"media-audio"
"media-image"
"media-video"
"conversation-chatgpt"
"conversation-windows-codex"
"device-pc"
```

### commands

Timeline から呼ぶ操作面です。

原則:

- 起動は `start.ps1`
- 停止は `stop.ps1`
- CLI は `cli.ps1`
- Timeline は Docker コンテナへ直接入らない
- Timeline はサブ製品を操作するとき、CLI がある場合は `cli.ps1` を優先する

### settings

サブ製品の設定ファイルです。

通常は以下です。

```json
{
  "file": "settings.json"
}
```

`supportsBasicDefaults` は、Timeline の基本設定から初期値を流し込めるかを示します。

`supportsProductOverrides` は、製品ごとの個別上書き設定を受け付けられるかを示します。

### capabilities

Timeline UI で表示できる機能です。

| 項目 | 意味 |
|---|---|
| fileList | 元ファイル一覧を取得できる |
| itemList | 管理対象アイテム一覧を取得できる |
| itemRefresh | スキャン / 取り込み / 分析を実行できる |
| itemDownload | 生成データをダウンロードできる |
| itemRemove | 管理対象データを削除できる |
| modelList | 利用 AI モデル一覧を取得できる |
| verbalization | 音声または動画のフォントークンを言語化できる |

未対応の機能は `false` にします。
未確定の場合は、無理に `true` にしません。

### data.sourcePathsFromSettings

ユーザーの元ファイルがある場所を、settings.json のどこから読めるかを指定します。

例:

```json
[
  "inputRoots[].path"
]
```

このパスは、アンインストールで削除してはいけない元データの判定に使います。

### data.generatedPathsFromSettings

サブ製品が生成したデータやマスターを、settings.json のどこから読めるかを指定します。

例:

```json
[
  "outputRoots[].path"
]
```

このパスは、アンインストール確認画面で「取り込んだデータも削除する」を選んだときの候補になります。

ただし、Timeline 側は必ず安全チェックを行います。

- 元ファイルの場所と重なっていないか
- ドライブ直下ではないか
- Timeline 本体ではないか
- 予期しない広すぎるディレクトリではないか

### uninstall

アンインストール時の扱いです。

原則:

- 製品アプリ本体は削除対象
- 元ファイルは削除しない
- 設定は退避が初期値
- 生成データは残すのが初期値
- 生成データ削除はユーザーが明示した場合だけ

### runtime

実行環境に関する情報です。

`usesDocker` は、その製品が Docker を使うかどうかです。

`dockerManagedByTimeline` は、Timeline が Docker 関連リソースの削除まで管理してよいかどうかです。

初期段階では、原則 `false` を推奨します。

```json
{
  "usesDocker": true,
  "dockerManagedByTimeline": false
}
```

Docker リソース削除の契約が固まっていない状態で `true` にしないでください。

## サブ製品へ投げる依頼プロンプト

以下を各サブ製品の管理スレッドに貼り付けて使います。

```text
Timeline 本体からこのサブ製品を安全に管理するため、リポジトリ直下に `timeline-product.json` を追加してください。

目的:
- Timeline 側がサブ製品の起動、停止、CLI、設定ファイル、生成データ、入力データ、アンインストール時の扱いを推測しないようにする
- 製品ごとの差異を、共通形式のマニフェストで明示する
- Timeline 本体から安全にインストール、起動、停止、設定、スキャン、ダウンロード、アンインストールを扱えるようにする

ファイル名:

```text
timeline-product.json
```

配置場所:

```text
リポジトリ直下
```

必ず守ること:
- `manifest.json` という名前にはしない
- Timeline は Docker コンテナへ直接入らない前提にする
- 起動は `start.ps1`
- 停止は `stop.ps1`
- CLI がある場合は `cli.ps1`
- ユーザーの元ファイルはアンインストール削除対象にしない
- 生成データは、ユーザーが明示的に選んだ場合だけ削除対象にする
- Docker 関連リソース削除の契約が未確定なら `dockerManagedByTimeline` は `false` にする

作ってほしい内容:

1. 現在の README と settings.json を確認する
2. この製品の productId を決める
3. 起動、停止、CLI の相対パスを書く
4. settings.json の相対パスを書く
5. 入力元ディレクトリを settings.json のどの項目から読めるかを書く
6. 生成データ / マスター / 出力ディレクトリを settings.json のどの項目から読めるかを書く
7. 対応している機能を capabilities に書く
8. アンインストール時の扱いを書く
9. Docker 関連を Timeline が管理してよいかを書く
10. JSON として妥当な `timeline-product.json` を追加する

雛形:

```json
{
  "schemaVersion": 1,
  "productId": "<product-id>",
  "displayName": "<display-name>",
  "productKind": "<product-kind>",
  "description": "<short-description>",
  "commands": {
    "start": {
      "type": "powershell",
      "path": "start.ps1"
    },
    "stop": {
      "type": "powershell",
      "path": "stop.ps1"
    },
    "cli": {
      "type": "powershell",
      "path": "cli.ps1"
    }
  },
  "settings": {
    "file": "settings.json",
    "supportsBasicDefaults": true,
    "supportsProductOverrides": true
  },
  "capabilities": {
    "fileList": false,
    "itemList": false,
    "itemRefresh": false,
    "itemDownload": false,
    "itemRemove": false,
    "modelList": false,
    "verbalization": false
  },
  "data": {
    "sourcePathsFromSettings": [],
    "generatedPathsFromSettings": [],
    "deleteSourceData": false
  },
  "uninstall": {
    "deleteAppDirectory": true,
    "backupSettingsByDefault": true,
    "deleteGeneratedDataOptional": true,
    "deleteGeneratedDataByDefault": false,
    "deleteSourceData": false
  },
  "runtime": {
    "usesDocker": true,
    "dockerManagedByTimeline": false
  }
}
```

productId の候補:
- TimelineForAudio: `audio`
- TimelineForImage: `image`
- TimelineForVideo: `video`
- TimelineForWindowsCodex: `windows-codex`
- TimelineForChatGPT: `chatgpt`
- TimelineForPC: `pc`

作業後に確認してほしいこと:
- JSON として壊れていないこと
- README の実際の CLI / settings.json と矛盾していないこと
- 元ファイルのディレクトリと生成データのディレクトリを混同していないこと
- アンインストールで元ファイルを削除対象にしていないこと
- `dockerManagedByTimeline` を安易に true にしていないこと
```

## 今後の Timeline 側実装方針

1. 既存の製品レジストリでインストール先と入手元を管理する
2. インストール後にサブ製品の `timeline-product.json` を読む
3. 起動 / 停止 / CLI / 設定 / 生成データ候補をマニフェストから解決する
4. マニフェストがない製品は、既存の互換処理で扱うが警告を出す
5. 各サブ製品に `timeline-product.json` が揃ったら、推測コードを減らす
