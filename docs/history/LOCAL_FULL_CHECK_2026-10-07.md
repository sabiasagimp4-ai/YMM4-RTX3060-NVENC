# RTX 3060 ローカル全検査の中断記録（2026-10-07）

利用者の停止指示により、2026-10-07 14:41 JST に検査プロセスとその子プロセスを終了した。全検査の完了報告ではない。製品コード・検査コードの修正は行っていない。

## 検査対象と環境

- 対象コミット: `915270c4a6d40fd3e4f7d238efe0d9a2516635c2`
- ブランチ: `claude/ymm4-nvenc-perf-analysis-5h1aik`
- プラグイン: `0.2.0-preview.5+915270c`
- 実行: `tools/local-full-check.ps1` の既定値（latest / rtx / gui / versions、NVENC 有効）
- 再実行期間: 14:21～14:41 JST、約20分
- RTX 3060 / 12 GB、NVIDIA driver 610.62
- Windows 11 Home 10.0.26200、PowerShell 5.1.26100.9444、.NET SDK 10.0.401
- Core i7-13700KF、RAM 47.9 GB、1920×1080
- Python 3.13.3、FFmpeg / ffprobe 8.1、jq 1.8.2

普段使う YMM4、マウス・キーボード、Windows の設定は操作していない。テスト用ホストは作業フォルダー内に取得した。停止後、追跡ファイルの変更がないことと YMM4 プロセスが残っていないことを確認してから、この記録を追加した。

## ここまでの結果

`steps.jsonl` の各 Id の最新レコードでは、setup 4項目・latest 19項目がすべて PASS。rtx は実行中に中断したため、親スクリプトによる結果レコードは未生成。

| 範囲 | 結果 |
| --- | --- |
| YMM4 4.56.1.0 の取得、パッケージ、検査プロジェクト、動画素材 | 4項目 PASS |
| YMM4 4.47.0.0 の取得・読み込み、4.56.1.0 の読み込み | PASS |
| ネイティブ不変条件、依存キー、ホスト統合 | PASS |
| PSD 重複 parser / source、PSD 立ち絵、大容量 PSD、大容量の動く立ち絵 | PASS |
| Store / Readiness / CacheAdversarial / FileLease / DescriptionJson | PASS |
| 検証ガードを意図的に除去する negative control | PASS、元のソースへ復元済み |
| --gpu、GPU 保持・先読み・デバイス喪失通知、2本・4本の idle 描画器 | PASS |
| 1000回の編集照合、プレビュー性能、動く立ち絵の追加検査 | PASS |

HostCacheProbe の --gpu は WARP と host graphics の検査を含む。とくに大容量立ち絵 fixture とログの WARP 性能値はソフトウェア描画の結果であり、RTX 3060 の GUI FPS・音声同期・Present の証明にはならない。

## rtx-check の途中結果

子スクリプトの標準出力と個別ログから確認した結果:

| Id | 状態 | 内容 |
| --- | --- | --- |
| build / native-checks | PASS | パッケージとネイティブ不変条件 |
| managed-smoke | FAIL | HostApi 初期化時に YukkuriMovieMaker アセンブリを読み込めない |
| audio-first | SKIP | managed-smoke が出力を書かなかったため |
| store-checks / readiness-checks / file-lease-checks / cache-checks | PASS | 各検査 |
| host-gpu | FAIL | プロセスの検査ではなく、スクリプトの期待出力文字列の不一致 |
| host-integration | PASS | 出力 scope 等の統合 |
| native-smoke | PASS | 実 NVENC の H.264 / HEVC + AAC、FFmpeg デコード、取消・失敗 |
| preview-1 / preview-2 / preview-3 | FAIL | 既存の performance-trace.jsonl と衝突 |
| benchmark | 中断 | 1080p、300フレーム、7条件×3回の検査中に停止 |

VUI 実験、GUI、全55版の互換性検査は未実施。GUI は現在の画面条件では SKIP となる想定だが、その分岐まで実行していない。

### 次回調べる箇所

1. **managed-smoke のホスト依存解決。** `HostApi` の型初期化で `YukkuriMovieMaker, Version=4.47.0.0` の FileNotFoundException。ManagedSmoke の出力には YukkuriMovieMaker.Plugin.dll があるが、YukkuriMovieMaker.dll はなく、csproj にも後者の直接参照がない。製品のホスト内ロードは通っている。検査側の依存解決を直し、実エンコードまで再確認する必要がある。
2. **host-gpu の古い期待文字列。** rtx-check は `Idle pre-render: independent scene clone, cancelled commit guard and unverifiable frames passed over OK` を要求するが、現在の検査は `Idle pre-render: independent scene clone, cancelled commit guard, unverifiable frames passed over and identity-random frames rendered from the live scene OK` を出す。検査の内容を弱めずに、期待文字列を現行出力へ合わせる必要がある。
3. **反復性能測定の出力名。** `CacheTrace.Session.WriteAsync()` が既存の `dist/performance-trace.jsonl` に対する IOException で終了する。latest の性能検査後に rtx が同じ fixture を繰り返すため衝突する。検査側で実行ごとの新しい出力先を使うなどの対処が必要で、製品の既存ファイル保護を外すべきではない。

上記の修正・追加検査は、利用者の停止指示後には行っていない。Python の `tests/tools` suite も今回未実施。

## 前の試行と取得失敗

最初の `6ec0164` では Windows PowerShell 5.1 の文字コード由来の構文エラーで、検査開始前に停止した。利用者が push した `915270c` で解消。

14:05 の試行では YMM4 の4ファイル（System.Net.Http.dll / System.Net.Http.Json.dll / System.Net.WebProxy.dll / System.Net.WebSockets.Client.dll）が接続タイムアウトになり、setup-ymm4 が FAIL、残りの setup 3項目が PASS で終了した。

取得先を Windows / Git の curl で確認し、HEAD と実際の GET が成功したため、14:21 に既存スクリプトを再実行した。取得済みファイルのサイズ・ハッシュを確認しながら再利用して setup-ymm4 が PASS になった。ダウンローダーやセキュリティ設定は変更していない。準備時の追加インストールは jq のみ（winget の msstore 証明書エラー後、winget source を明示して成功）。

## 手元の証拠と再開

以下は利用者の PC に残したローカル成果物であり、GitHub には添付していない。

- 実行フォルダー: `C:\ymm4-full-check\runs\915270c4a6d4`
- 再実行の起動ログ: `C:\ymm4-full-check\launcher-20261007-142118.stdout.txt` / `.stderr.txt`
- rtx の途中ログ: `C:\work\YMM4-RTX3060-NVENC\dist\rtx-check\20261007-143757`
- 親 rtx の実行中出力: 実行フォルダーの `logs\rtx-check.log.stdout`

実行フォルダーの既存 `summary.md` と `logs.zip` は14:05の取得失敗時の成果物で、14:21の再実行結果ではない。今回の再実行は最終まとめの前に中断しており、`rtx-check-logs.zip` も未生成。`progress.txt` の `running rtx-check` は停止前の表示で、プロセスはすでに終了している。最新の完了手順は `steps.jsonl` の Id ごとの最後のレコードで判断する。生ログ・ZIP には元のパスが残るため外部へ公開しない。

同じ **915270c** で再開すれば、完了済みの latest 手順は既存の記録から再利用できる。setup は再実行され、未完了の rtx から続く。この記録のコミットで HEAD が変わるため、現在の HEAD で起動すると別の実行フォルダーになる。同じコミットで続ける場合は、既存 checkout を reset せず、915270c の独立 worktree を作ってそこで同梱スクリプトを実行する。修正後の異なるコミットの結果と、915270c の結果は混ぜない。
