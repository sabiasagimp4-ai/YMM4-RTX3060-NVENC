# 資料の一覧

使い方と対応範囲は、まず [README](../README.md) を参照してください。ここでは `docs/` の資料を、今の仕様・手順と、過去の記録に分けています。

## 仕様

| 資料 | 内容 |
| --- | --- |
| [キャッシュ仕様](CACHE_BEHAVIOR.md) | 描画キャッシュのキー・保存・先読み・乱数などの今の仕様 |
| [自動対象の範囲](AUTOMATIC_COVERAGE_2026-10-03.md) | 自動でキャッシュの対象になるもの・ならないものと、その理由（2026-10-03 時点。その後の変更は冒頭の注） |
| [ホスト契約と更新手順](HOST_CONTRACTS.md) | YMM4 のどのコードを前提にしているか、古い版の扱い、YMM4 が更新されたときの手順 |
| [YMM4 の版ごとの対応](YMM4_VERSIONS.md) | 公開されている全版での読み込み・NVENC 出力・描画キャッシュの結果（自動生成） |
| [GPU 保持の設計](GPU_FRAME_RETENTION.md) | 描画済みフレームを GPU 上に残す仕組みと VRAM の配分 |
| [動的な計算共有などの契約](DYNAMIC_CACHE_API.md) | 外部から計算・依存を報告するための API の契約 |

## 開発・検証の手順

| 資料 | 内容 |
| --- | --- |
| [開発状況・次の課題](AE_CACHE_DEVELOPMENT.md) | After Effects のキャッシュに近づける開発方針と、残っている課題 |
| [AE SDK との契約差](AE_CACHE_CONTRACTS.md) | After Effects の SDK の約束と、このプラグインのキャッシュの違い |
| [詳細ログの採取と集計](CACHE_DIAGNOSTICS.md) | 処理ログの取り方、集計スクリプト、実 GUI での負荷試験 |
| [実機検証の手順](RTX3060_HARDWARE_CHECK.md) | GitHub Actions と RTX 3060 の PC での検証の分担と手順。すべての検査を PC で行う `tools/local-full-check.ps1` |
| [実ホスト検証の実行方法](../tests/HostCacheProbe/README.md) | 実際の YMM4 を使う検査（HostCacheProbe）の動かし方 |
| [リリースノート](release-notes/) | 各リリースの変更点（GitHub の Releases と同じ内容） |
| [立ち絵とアイテムの重なりの対応計画](TACHIE_PLAN_2026-10-06.md) | グループ制御・シーン・差分合成・付属 INI・同じレイヤーの重なり・まばたき・古い版の立ち絵の計画と、その実装の結果 |

自動で作るもの: `YMM4_VERSIONS.md` と `compat/ymm4-versions.json` は ymm4-compat ワークフロー（`tools/compat/ymm4_compat.py`）が、`assets/readme/` は `tools/readme-assets.py` が書きます。手で編集しないでください。

## 過去の記録

[history/](history/) には、その時点の調査・計測・レビューの記録を残しています。書いた時点の状態の説明なので、今の仕様とは違うところがあります（今の仕様は上の資料が正です）。

| 日付 | 資料 | 内容 |
| --- | --- | --- |
| 2026-10-01 | [処理ログの観測結果](history/CACHE_TRACE_RESULTS_2026-10-01.md) | 実 YMM4 の処理ログから見た挙動と、次の実装の判断 |
| 2026-10-01 | [GPU 保持の実測](history/GPU_FRAME_RETENTION_RESULTS_2026-10-01.md) | GPU 上に残したフレームによる復元の速さ |
| 2026-10-02 | [性能調査](history/PERFORMANCE_REVIEW_2026-10-02.md) | 重い処理の洗い出し |
| 2026-10-02 | [性能修正の結果](history/PERFORMANCE_RESULTS_2026-10-02.md) | 修正前後の測定 |
| 2026-10-02 | [非待機 readback](history/NONBLOCKING_READBACK_RESULTS_2026-10-02.md) | ライブプレビューの読み戻しを待たない変更の測定 |
| 2026-10-02 | [負荷試験](history/STRESS_GUI_RESULTS_2026-10-02.md) | 30 秒・421 アイテムのプロジェクトを実 YMM4 で操作した結果 |
| 2026-10-02 | [RTX 3060 の測定値](history/preview-performance-rtx3060.json) | 統合前の RTX 3060 でのプレビュー測定（JSON） |
| 2026-10-03 | [問題点の洗い出し](history/ISSUE_REVIEW_2026-10-03.md)・[3 回目](history/ISSUE_REVIEW_3_2026-10-03.md) | コードの見直しで見つけた問題と対応 |
| 2026-10-04 | [アーキテクチャ監査](history/CACHE_ARCHITECTURE_AUDIT_2026-10-04.md) | キャッシュの設計の前提の確認 |
| 2026-10-04 | [外部プラグインの判定](history/EXTERNAL_PLUGINS_2026-10-04.md) | 外部プラグインを自動で対象にする方法の設計案と実験 |
| 2026-10-04 | [立ち絵の口パク](history/LIPSYNC_RESEARCH_2026-10-04.md) | 立ち絵の口パクをキャッシュするための調査 |
| 2026-10-04 | [レビュー指摘の修正](history/REVIEW_FIXES_2026-10-04.md) | レビュー指摘の統合 |
| 2026-10-04 | [高速化の計画](history/SPEEDUP_PLAN_2026-10-04.md)・[依頼文](history/SPEEDUP_REQUEST_CHATGPT.md)・[引継ぎ](history/SPEEDUP_HANDOFF_CHATGPT_2026-10.md) | 描画キャッシュの高速化の計画と、実装の依頼・引継ぎ |
| 2026-10-05 | [高速化の結果](history/SPEEDUP_RESULTS_2026-10.md)・[レビュー](history/SPEEDUP_REVIEW_CLAUDE_2026-10-05.md)・[レビューへの対応](history/SPEEDUP_REVIEW_RESPONSE_2026-10-05.md) | 高速化 PR（#5〜#13）の結果とレビュー |
| 2026-10-05 | [GPU 自動配分のレビュー対応](history/GPU_BUDGET_REVIEW_2026-10-05.md) | VRAM の自動配分のレビュー対応 |
| 2026-10-01〜02 | [トレース](history/traces/) | 上の記録の元になった処理ログ・測定の生データ |
