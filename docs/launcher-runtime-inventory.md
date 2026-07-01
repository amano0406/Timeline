# Timeline ランチャー/ランタイム整理

この文書は、Timeline をユーザーから見て「1つのアプリ」として起動、停止、復旧できる状態にするための整理資料である。
Jira では主に `KAN-5` と `KAN-6` の前提資料として扱う。

## 現在の入口

| 入口 | 役割 | 扱い |
| --- | --- | --- |
| `start.bat` | Windows ユーザー向けの起動入口 | `start.ps1` を呼ぶ薄い入口 |
| `start.ps1` | Timeline 本体の起動 | Docker、Local API、Web、Worker、Ollama を起動する中心 |
| `start.sh` | macOS / Linux 向けの最小起動入口 | 既定ポートまたは環境変数で Local API、Docker、Web、Worker、Ollama を起動する |
| `stop.bat` | Windows ユーザー向けの停止入口 | `stop.ps1` を呼ぶ薄い入口 |
| `stop.ps1` | Timeline 本体の停止 | Docker compose と Local API を停止する |
| `stop.sh` | macOS / Linux 向けの最小停止入口 | Docker compose と `start.sh` が起動した Local API を停止する |
| `TimelineLauncher.bat` | Windows ユーザー向けの通常入口 | Webが起動済みなら開き、未起動なら起動する。`status` / `start` / `stop` も扱う |
| `TimelineStatus.bat` | Windows ユーザー向けの状態確認入口 | Web未起動時でも Local API / Docker / Web の状態を確認する |
| `TimelineStop.bat` | Windows ユーザー向けの停止入口 | ユーザーが PowerShell を開かずに停止できる入口 |
| `timeline-launcher.sh` | macOS / Linux 向けの通常入口 | `open` / `status` / `start` / `stop` を扱う薄い入口 |
| 設定画面の OS 起動時自動起動 | OS 起動時に Timeline を起動する | Windows Startup folder 実装が中心 |
| スキャン画面の復旧操作 | Worker 停止時の部分復旧 | Timeline worker だけを再起動する |

## 起動時の責務

`start.ps1` は単なる Docker 起動スクリプトではなく、現在は以下の責務を持つ。
`start.sh` はこのうち macOS / Linux で最小起動に必要な部分だけを持つ。
`settings.json` の完全な補完や Windows 固有の Docker Desktop 起動補助は、現時点では `start.ps1` 側が中心である。

1. `settings.json` の `runtime` 設定を補完する。
2. `runtime.instanceName` から Compose project name と image tag を決める。
3. Web、Local API、Ollama のポートを決める。
4. Docker CLI と Docker Desktop の状態を確認する。
5. Local API を起動する。
6. `data/work` と `data/to_timeline` を準備する。
7. Ollama volume を確認する。
8. `docker compose up -d --build --remove-orphans ollama web worker` を実行する。
9. Ollama API とモデルを確認する。
10. Web UI と Local API のヘルスチェックを行う。
11. 接続済みサブ製品の検出状態を表示する。

## Docker 側の構成

| service | 役割 | 外部公開 |
| --- | --- | --- |
| `ollama` | LLM 実行基盤 | `127.0.0.1:${TIMELINE_OLLAMA_PORT}` |
| `web` | Blazor Web UI | `127.0.0.1:${TIMELINE_WEB_PORT}` |
| `worker` | Timeline store の状態監視と内部処理 | 外部公開なし |

永続化は主に以下で扱う。

| 保存先 | 用途 |
| --- | --- |
| bind mount `data/work` | 作業データ、一時生成物、ダウンロード出力 |
| bind mount `data/to_timeline` | Timeline store |
| Docker volume `timeline-ollama` | Ollama モデル |

## Local API の位置づけ

Local API は Docker 外のホスト側で動作する。
主な理由は、ホストOS上でしか自然に扱えない処理があるためである。

- OS のファイル選択、ディレクトリ選択
- `start.ps1` / `stop.ps1` の実行
- OS 起動時自動起動の登録
- ホスト上のサブ製品起動、停止、状態確認
- Web コンテナから `host.docker.internal:${TIMELINE_LOCAL_API_PORT}` 経由で呼ばれる処理

Local API はすぐには消せない。
消すよりも、ユーザーから見える責務と内部責務を分け、将来的なランチャーやRuntime APIへ寄せる方が現実的である。

## Local API の起動方式

現在のWindows環境では、未署名の生成物を直接実行すると、Code Integrity により以下のようにブロックされる場合がある。

- `0x800711C7`
- `アプリケーション制御ポリシーによってこのファイルがブロックされました`
- `did not meet the Enterprise signing level requirements`

確認済みのリスク:

- `bin/Debug` の `Timeline.LocalApi.dll` を直接 `dotnet` で読むとブロックされる場合がある
- `PublishSingleFile=true` で作成した `Timeline.LocalApi.exe` も、未署名EXEとしてブロックされる場合がある

そのため、`start.ps1` は Local API を Release publish した DLL として出力し、Microsoft署名済みの `dotnet.exe` から起動する。

現在の方針:

1. `dotnet publish local-api/Timeline.LocalApi.csproj -c Release -p:UseAppHost=false`
2. 出力先は `.local/local-api-build-${port}`
3. `dotnet.exe .local/local-api-build-${port}/Timeline.LocalApi.dll` として起動する
4. `.local/` は生成物なので Git 管理しない

この方式は、現時点のWindows環境で実際に起動確認済みである。

## macOS / Linux 最小入口

`start.sh` / `stop.sh` は、Mac対応の成立性を作るための最小入口として扱う。
現時点では Windows 版 `start.ps1` と完全同等ではない。

主な責務:

1. Local API を Release publish した DLL として起動する。
2. `TIMELINE_WEB_PORT`、`TIMELINE_LOCAL_API_PORT`、`TIMELINE_OLLAMA_PORT`、`TIMELINE_COMPOSE_PROJECT` などの環境変数を受け取る。
3. `data/work` と `data/to_timeline` を準備する。
4. 外部 Docker volume `timeline-ollama` を準備する。
5. `docker compose up -d --build --remove-orphans ollama web worker` を実行する。
6. Web UI と Local API のヘルスチェックを行う。

制約:

- `settings.json` の `runtime.instanceName` 補完は行わない。
- ポートや Compose project name は、環境変数がなければ既定値を使う。
- Docker Desktop の自動起動は行わない。
- サブ製品の macOS / Linux ランチャーは、各サブ製品側に `start.sh` / `stop.sh` が必要である。

## 停止時の責務

`stop.ps1` は以下だけを担当する。

1. `docker compose down --remove-orphans`
2. Local API プロセスの停止
3. compose down が失敗した場合の最低限の警告表示

停止スクリプトは Local API を起動しない。
停止スクリプトは Docker Desktop も起動しない。Docker Engine がすでに利用できる場合だけ compose down を試し、利用できない場合は Docker 側の停止処理をスキップして Local API 停止だけ行う。
ただし、過去の起動方式で残っている Local API プロセスも安全に止められるように、`Timeline.LocalApi.exe` と `Timeline.LocalApi.dll` の両方を検出対象に含める。

## 復旧導線

現在、スキャン画面では Timeline worker の状態を表示する。

状態判定は以下を組み合わせる。

1. Worker が書く heartbeat
2. Docker コンテナ実体の状態

heartbeat だけだと、Worker停止直後に最大30秒ほど `稼働中` に見える可能性がある。
そのため、Local API の状態APIでは Docker コンテナ実体も短時間で確認し、コンテナが `exited` の場合は即座に復旧対象として返す。

画面上の復旧操作は、Timeline worker だけを再起動する。
Web、Ollama、サブ製品全体を巻き込まない。

確認済みの復旧動作:

1. `timeline-<instance>-worker-1` を停止する。
2. `GET /timeline/worker/status` は、heartbeat が残っていても Docker state `exited` を見て `available=false` / `state=stale` を返す。
3. `POST /timeline/worker/repair` は `docker compose up -d --build worker` 相当を実行し、Worker だけを再作成して起動する。
4. 復旧後の `GET /timeline/worker/status` は `available=true` / `state=running` を返す。
5. スキャン画面では `自動処理を復旧` ボタンが表示され、クリック後に `稼働中` 表示へ戻る。

この復旧は、Timeline store の再構築やサブ製品の起動停止を行うものではない。

## Web外ランチャーの状態確認

`TimelineLauncher.bat status` / `TimelineStatus.bat` / `timeline-launcher.sh status` は、Web画面が開けない状態でも最初の状態確認入口として扱う。

状態確認は次の順で行う。

1. Local API の Runtime状態APIを読める場合は、その結果を正本として表示する。
2. Runtime状態APIを読めない場合は、Web、Local API、Docker CLI の最小確認に fallback する。
3. Docker CLI の確認では、Docker Desktop の既定配置と PATH を順に探す。
4. Docker の失敗は、少なくとも `command_missing`、`engine_stopped`、`timeout`、`unknown` に分ける。

fallback 表示の目的は、ユーザーに「Timeline が壊れている」のか「Docker を起動すればよい」のかを分けて伝えることである。
そのため、Docker Engine が止まっている場合は、Docker Desktop を起動してから `TimelineLauncher status` または `TimelineLauncher open` を再実行するよう案内する。

確認済みの fallback 動作:

1. 未使用ポートの `settings.json` を持つ一時 root で、Local API 不通相当を作る。
2. Docker が動いている状態では `Docker: running` と Timeline 関連コンテナ一覧を表示する。
3. `DOCKER_HOST` を存在しない named pipe に向け、実 Docker を止めずに Docker Engine 不通相当を作る。
4. この状態では `Docker: engine_stopped` と Docker Desktop 起動案内を表示する。

## 現在の弱点

| 弱点 | 影響 |
| --- | --- |
| `start.ps1` の責務が大きい | 失敗時にどこで失敗したかユーザーが判断しづらい |
| Local API がホスト側に残っている | 完全なDocker完結構成ではない |
| Windows前提の起動方式が多い | macOS対応時にLaunchAgent等の別実装が必要 |
| Runtime状態の表示が分散している | Web、Worker、Local API、Docker、Ollamaの関係が見えづらい |

## ランチャー化の基本方針

既存の `start.ps1` / `stop.ps1` をすぐ廃止しない。
まず責務を次の層に分ける。

| 層 | 責務 |
| --- | --- |
| ユーザー向けランチャー | 起動、停止、状態確認、復旧、設定、ログ表示 |
| Runtime API | Web、Worker、Local API、Docker、Ollama、サブ製品の横断状態 |
| 既存スクリプト | 実際の低レベル起動停止 |
| Docker Compose | Web、Worker、Ollamaの実行単位 |

重要なのは、ユーザーに `start.ps1` の内部責務を理解させないことである。
商品化する場合は、ユーザー向けにはランチャーまたはWeb上の復旧UIを前面に出し、`start.ps1` / `stop.ps1` は内部用に下げる。

現時点の最小ランチャーは `launcher/Timeline.Launcher.csproj` で実装する。
このランチャーはネイティブアプリ完成版ではなく、Webがまだ開けない状態でも `open` / `status` / `start` / `stop` をユーザー語彙で実行するための薄い入口である。
将来的なデスクトップショートカット、スタートメニュー、トレイ常駐、Mac の LaunchAgent などは、この薄い入口の責務を土台にして別タスクで扱う。

## 次にやること

1. Runtime状態APIを、Web / Worker / Local API / Docker / Ollama / サブ製品で統一する。
2. ダッシュボードとスキャン画面の復旧導線を同じ語彙にそろえる。
3. Local APIが起動しない場合の復旧導線を設計する。
4. macOS対応はEpicのまま残し、現時点ではWindowsの起動停止品質を優先する。
