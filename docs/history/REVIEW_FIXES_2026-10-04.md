# レビュー指摘の修正（2026-10-04）

起点は `main` の `8aa03b2` と、計画・依頼文を追加した `1cd7b21`。既存の draft PR #4 (`ea12e9d`) の修正を、新しいブランチへ統合した。main に追加された idle の乱数フレーム検査と、監査 PR の操作列検査を両方残した。既存ブランチは変更していない。

| 指摘 | 修正 |
| --- | --- |
| 同じレイヤーの重なるアイテムを並べ替えてもキーが同じ | PR #4 の保守的な区間単位の除外を維持。描画順を証明するまでは保存しない |
| capture 後の編集でも直前の live 出力を再利用 | `StillCurrent` を再利用直前に検査。検査を外すと失敗する対照も維持 |
| HEVC の MP4 設定が固定の level 4.0／単色 | encoder の SPS から profile、互換性、制約、level、色形式、bit depth、temporal layer 数を読む。壊れた設定を公開しない |
| disk record が要求キーと結び付いていない | PR #4 のキー＋画素の checksum と 02 schema。旧 record は再描画 |
| 別スレッドの計算で循環待ち | PR #4 の待機 edge 検査。循環になる場合は待たずに値なしを返す |
| provider の状態を並べ替えると同じキー | PR #4 の slot 順を保持する識別 |
| idle 描画中の消去を越えて公開できる | 描画の前に取る `PrimeTicket` を維持 |

HEVC の検査は、レビューで作った libx265 の 3840×2160、Main、yuv420p、level 5.0 の VPS／SPS／PPS を使う。YMM4 のバイナリではない。設定の主要 23 byte、パラメーター集合の一致、切れた入力、サイズ上限、壊れた emulation-prevention を Linux の C++ と Windows の NativeChecks で検査する。

この修正は、host のモデル読み取り全体の原子性、手動で信頼した外部コードの純粋性、実 RTX 3060 の動作を証明するものではない。高速化はこの状態を起点に、別の積み重ねた draft PR で進める。
