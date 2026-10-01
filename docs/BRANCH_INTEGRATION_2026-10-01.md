# ブランチ統合（2026-10-01）

今後の開発と検証の基準は `main` とする。統合前の全4ブランチを比較し、以下の順に祖先関係が成立することを確認した。分岐した未統合の変更はない。

| 統合前ブランチ | HEAD | 直前のブランチからの追加 | 主な内容 |
| --- | --- | ---: | --- |
| main | 37c60524cab4a3eff6903ccec29b58c0c784882e | — | 従来のNVENC出力・キャッシュ |
| claude/frame-render-readiness | 56e94419a668dfde8f9296d1aeb2f62fb8c5a13a | 94コミット | デコード完了判定、フレーム依存キー、キャッシュバー、ホスト互換性 |
| codex/preview-scheduler | e60c455217179b49f567cd79e5a677d1692f7aa8 | 1コミット | プレビュースケジューラー、計測 |
| codex/ae-cache-continuation | 784347f9e18624df546eb682384fe51593d5e86d | 13コミット | 動的な処理計測、RAM調整、全区間idle先読み、GPU再利用、利用履歴による保持判断 |

PR #1 を `main` にマージし、PR #2 のベースを `main` に変更して統合する。マージ方式は merge commit とし、元の全コミットと既存PRの履歴を残す。スケジューラーのコミットもPR #2に含まれる。

既存の開発ブランチは統合時点の参照として残す。以後、新しい作業は更新した `main` を起点とする。SDK参照用の `aesdk` と実ホスト検証用の `YMM4-dlls` は別リポジトリであり、この統合の対象ではない。

## 継続検証

`cache-development` を `main` へのpushでも実行する。従来のportable・native・実ホスト・画素一致検証に加え、実ホストのWARPでGPU保持、借用中の退避、編集・purgeによる無効化、処理計測を検証し、`dist` の測定結果を14日間保存する。性能値にハードウェア依存の固定しきい値を設けない。

統合する実装の検証済みコミットは `a91e1c2c7e6fe29358ef97d196275687d9c013f1`。その後の元HEADは資料と計測データの追加のみ。

- [ソースCI（成功）](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/36875041175)
- [実ホスト検証・GPU保持計測（成功）](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36875040153)
- [実YMM4 GUIでのログ取得（成功）](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36875046663)

詳細は `GPU_FRAME_RETENTION_RESULTS_2026-10-01.md` と `CACHE_TRACE_RESULTS_2026-10-01.md`。RTX 3060上のNVENC出力および実プロジェクト全体の速度は、これらのWARP検証とは別に確認が必要。
