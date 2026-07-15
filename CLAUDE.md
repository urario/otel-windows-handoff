# otel-windows-handoff Claude Code Guide

Zenn コンテスト記事のサンプルコードリポジトリ(公開・読者が clone する前提)。企画・タスク管理・実験データの正本は zenn-content リポジトリの Issue(親: urario/zenn-content#30)にある。

## 役割

- Claude Code はこのリポジトリでは**仕様書執筆・PR レビュー・環境整備**を担当する。機能実装は Codex の担当(軽微な修正・開発環境ファイルの整備は Claude が行ってよい)
- 実験結果・Go/No-Go 判定などの一次データは zenn-content 側 Issue のコメントに置く。このリポジトリには置かない
- 決定・教訓のナレッジも zenn-content の `knowledge/`(DEC / LES)に集約する(zenn-content#55 の運用)

## PR レビュー

- `.claude/skills/spec-conformance-review` の観点に従う(仕様 Issue の受け入れ条件との突合・禁止事項スキャン・OTel 計装設計・再現性)
- `src/App.WinUI` は「Codex が Linux でビルド未検証のまま出す」前提で届く。静的に分かる問題の指摘に徹し、動作確認は著者実機検証(zenn-content#50)へ委ねる

## 記事への逆流ガード

- このリポジトリ固有の内部パス・一時的な ID・検証環境の情報を記事(zenn-content)へ持ち込まない。逆流検知パターンは zenn-content の `tools/backflow-rules.json`
- 記事から本リポジトリへの clone URL 案内は許可されている

## 禁止事項(AGENTS.md と共通)

- 秘密情報・個人名・マシン名・実在の社名/製品名・フルパスのハードコード
- バイナリ・テストデータのコミット
