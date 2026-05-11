# TimelineForPC integration TODO

## Goal

TimelineForPC を Timeline 本体の管理対象として追加する。

TimelineForPC は Docker worker 型ではなく、Windows ホスト上で PC 状態を取得する CLI 製品として扱う。
そのため、既存の Audio / Image / Video とは起動停止の考え方を分ける。

## Checklist

- [x] TimelineForPC の README / CLI / settings を確認する
- [x] CLI の基本動作を確認する
- [x] mock で refresh / download が通ることを確認する
- [x] Timeline 本体の製品レジストリに `pc` を追加する
- [x] 製品管理で TimelineForPC を表示する
- [x] TimelineForPC を「起動停止不要」の製品として扱う
- [x] 設定モーダルのプロモードに TimelineForPC を表示しても破綻しないようにする
- [x] スキャン画面に PC 状態の導線を追加する
- [x] ASCII チェックを通す
- [x] Web ビルドを通す
- [x] 実画面で製品管理・設定・スキャンを確認する
- [x] TimelineForPC を時間軸再構築・分析用データの対象へ正式に含める
- [x] TimelineForPC の専用設定UIを作る
- [x] TimelineForPC の一覧UIを作る。詳細UIは現時点では作らず、必要になったら既存成果物を読む専用APIとして追加する。

## Known facts

- Product path: `<Timeline>\data\products\TimelineForPC`
- CLI launcher: `<Timeline>\data\products\TimelineForPC\cli.ps1`
- Runtime kind: Windows host
- Docker is not required.
- Settings:
  - `output_root`
  - `redaction_profile`
  - `mock_profile`
- Production item count was 0 at the first check. Later checks returned 1 production item.
- Mock refresh and download worked.
- Timeline store rebuild completed with TimelineForPC in the product set.
- Production TimelineForPC was skipped because it currently has 0 items and no download ZIP.
- LLM input preview for `product=pc` returns an empty page instead of failing.

## Integration notes

- Do not start or stop Docker for TimelineForPC.
- Do not enter Docker containers.
- Use TimelineForPC only through `cli.ps1`.
- If content-rich analysis needs raw snapshot artifacts, confirm whether the product download ZIP should include them. The current checked mock ZIP includes timeline and metadata, but not every referenced artifact.
- A standalone PC list page exists. Detail is intentionally deferred because the current product role is a compact host-state capture and the list is enough to confirm import status.
