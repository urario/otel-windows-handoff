# otel-windows-handoff Agent Guide(Codex 向け)

このリポジトリは、Zenn コンテスト記事「Windows デスクトップアプリの障害解析に OpenTelemetry はどこまで使えるか」のサンプルコードです。読者が clone して再現する前提の公開リポジトリです。

## 最優先の価値基準

コードの洗練より、次の 3 つを優先します。

1. 障害を確実に再現できること(障害注入は決定的であること。同じ入力なら同じジョブが遅い/失敗する)
2. 手順の再現性(README のコマンドをコピペするだけで再現できること)
3. 計装設計が読んで分かること

## 役割分担

- **Codex(あなた)**: 実装担当。仕様書はタスク投入時に本文として渡される。本ファイルは仕様書に書かれない常設ルールを定める
- **Claude Code**: 仕様書執筆・PR レビュー・ナレッジ担当
- **著者**: Windows 実機での検証・実験実行・PR マージ

## リポジトリ構成(仕様書で指定。勝手に変更しない)

```
spike/                    # スパイク PoC(trace_id⇔ETW 接続検証)
src/Pipeline.Core/        # net10.0 クラスライブラリ(プラットフォーム中立)
src/Pipeline.Core.Tests/  # xUnit(Linux で実行可能)
src/App.Console/          # コンソールヘッド
src/App.WinUI/            # WinUI 3 シェル(net10.0-windows)
collector/                # otelcol-contrib 設定
```

## 検証範囲(重要)

- あなたの実行環境は Linux。**`Pipeline.Core` + `App.Console` + Tests のビルド・テスト通過(`dotnet build` / `dotnet test`)までがあなたの検証責任**
- `src/App.WinUI` は Linux でビルド検証できない。ベストエフォートで書き、CI の windows ジョブと著者の実機検証に委ねる
- Windows 専用 API を `Pipeline.Core` / Tests に持ち込まない(Linux でのビルド・テストを壊さない)

## 禁止事項

- 秘密情報・API キー・個人名・マシン名・実在の社名/製品名・フルパスのハードコード(コード・コメント・README・設定すべて。記事用スクリーンショットに映る前提で書く)
- Span タグへのフルパス混入(`file.name` はファイル名のみ)
- バイナリ・テストデータのコミット(`generate-data` コマンドで生成する)

## PR 運用

- トピックブランチで作業する。main への直接コミット禁止
- PR 説明に仕様書の受け入れ条件チェックリストを転記し、各項目の自己検証結果(通過 / Linux では未検証とその理由)を明記する
- 依存パッケージのバージョンは固定し、README のバージョン固定表を更新する

## 言語

コードコメント・README・PR 説明は日本語でよい。
