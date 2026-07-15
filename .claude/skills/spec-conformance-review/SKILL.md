---
name: spec-conformance-review
description: otel-windows-handoff への PR(主に Codex 実装)を仕様 Issue の受け入れ条件と突合してレビューする。禁止事項スキャン・OTel 計装設計・再現性・WinUI ビルド検証の自己申告確認の観点を含む。PR レビュー時に使う。
---

# Spec Conformance Review

## 使いどき

otel-windows-handoff への PR のレビュー。特に Codex 実装 PR(spike PoC、Pipeline.Core、測定スクリプト、README 整備)。

## 入力

1. 対象 PR の差分
2. 対応する仕様 Issue(zenn-content 側。例: #35 / #38 / #41 / #48)。PR 説明にリンクまたは受け入れ条件の転記があるはず。なければまずそれを指摘する

## レビュー手順

### 1. 受け入れ条件の突合(主目的)

仕様 Issue の受け入れ条件チェックリストを列挙し、差分と 1 対 1 で突合する。未達の条件、仕様にない追加実装、仕様と異なる挙動を指摘する。

### 2. 禁止事項スキャン(ブロッキング)

コード・コメント・設定・README・テストの全差分を対象に:

- 秘密情報・API キー・トークン
- 個人名・マシン名・ユーザー名(記事用スクリーンショットに映る前提)
- 実在の社名・製品名
- フルパスのハードコード(Span タグ `file.name` はファイル名のみ)
- バイナリ・テストデータのコミット

### 3. OTel 計装設計の妥当性

- `--otel off|sdk|otlp` の 3 モードが仕様通りか(**off は Provider を一切構築しない、sdk は Exporter 未登録**)。E3 導入コスト測定の条件そのものなので厳密に確認する
- 終了時 `ForceFlush`→`Shutdown` と flush 所要時間の標準出力(E3 でアプリ終了時間を測る前提)
- Span の親子関係・タグ設計(カーディナリティ、`job.id` / `file.name` / `retry.count`)
- 障害注入が決定的か(同じ入力で同じジョブが遅い/失敗する。記事スクショの再現に必須)

### 4. 再現性

- README の手順がコマンドコピペで通る粒度か
- バージョン固定表が差分の依存変更と一致しているか
- プラットフォーム中立の維持: `Pipeline.Core` + `App.Console` + Tests に Windows 専用 API が混入していないか(CI の linux ジョブ結果を確認)

### 5. WinUI(src/App.WinUI)の扱い

Codex はローカル(Windows)実行のため、ビルド・テスト検証済みで届く前提(PR の自己申告を確認。未検証なら指摘)。レビューでは:

- ビジネスロジックの WinUI 側への混入(Core 呼び出しと UI 状態管理以外があれば指摘)
- API 誤用・XAML と code-behind の不整合
- 実行時の動作保証までは求めない(起動・UI 操作・OTLP 送信の確認は著者実機検証 zenn-content#50 の領分)

## 出力

- **ブロッキング**(禁止事項違反・受け入れ条件未達・E3 測定条件の誤り)と**提案**を分けて報告する
- 受け入れ条件の突合結果を表(条件 / 差分上の根拠 / 判定)で示す
