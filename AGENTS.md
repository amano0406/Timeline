日本語で回答する。

このリポジトリでは、Windows PowerShell 5.1 から直接実行される `.ps1` を扱う。

`.ps1` ファイルは ASCII のみにする。UTF-8 BOM は先頭に限り許容するが、日本語などの非 ASCII 文字列は PowerShell スクリプト内に入れない。

ユーザー向けの日本語文言は Blazor/C# UI、JSON リソース、Markdown ドキュメントに置く。PowerShell 補助サーバーや起動スクリプトのエラーメッセージは ASCII の英語にする。

PowerShell スクリプトを編集したら、必ず次を実行して確認する。

```powershell
.\scripts\check-powershell-ascii.ps1
```

`start.ps1` と `stop.ps1` はこのチェックを起動前に実行する。チェックに失敗した場合は、非 ASCII 文字を PowerShell 以外の層に移す。

各サブ製品を操作する場合は、必ずその製品の `cli.ps1` 経由で行う。サブ製品の Docker コンテナへ直接入ってコマンドを実行しない。
