# AEキャッシュに近づける開発方針

2026-10-01。目標は、After Effectsに近いキャッシュの挙動をYMM4の標準プレビューへ組み込むこと。
今回の土台は `codex/preview-scheduler` の `e60c455217179b49f567cd79e5a677d1692f7aa8`。

## 調べたブランチ

| ブランチ | 確認時点 | 役割 |
|---|---|---|
| main | 37c60524 | NVENCと初期キャッシュ。Readinessの完成版は開発ブランチにある |
| claude/frame-render-readiness | 56e94419 | mainから94コミット。デコード完成判定、フレーム単位キー、編集/Undo、通常プレビュー保存、RAM/ディスク供給、帯、ホスト版の照合 |
| codex/preview-scheduler | e60c4552 | 上記から1コミット。Update/Draw/readback等の分解計測とRTX3060のsource-only測定、scheduler引継ぎ |

既存PR #1はdraft。独立した今回のPRは `codex/preview-scheduler` に対する差分にする。
NVENCのエンコード並列処理と、YMM4の描画並列処理は別物。MFRが実装済みという扱いにはしない。

## 今回の変更

- RAMの固定256MiBを可変化。既定の設定上限は2048MiB、自動配分は有効。64〜16384MiBで上限を変更でき、手動固定も選べる。
- 自動配分はレンダースレッド外で1秒ごとにOSの空き物理メモリ、プロセスprivate bytes、managed heap、GC上限を読む。OSに最大(1GiB, 物理RAMの1/8)を残し、書き込みキューと最大1フレーム分の余裕も確保する。
- キャッシュの上限は設定値と物理RAMの1/4以下。プロセス全体は物理RAMの1/2を目安に制限する。圧力時は直ちに縮小し、回復時は健康なサンプルが3回続いた後に128MiBずつ増やす。取得失敗では増やさない。これらは本プラグインの方針であり、AEの内部アルゴリズムを複製したものではない。
- LRU退避は参照を外すだけで借用中の画素を変更しない。RAM上限の縮小で、ディスク上の有効なレコードを削除しない。ディスク読み込み前とRAMへの昇格時に最新予算を確認する。
- 停止中キャッシュの10秒先・2分経過の打ち切りを削除。30フレームずつ、現在位置から末尾→先頭、現在位置の前後、先頭からの3順序でタイムライン全体を巡回する。既定の待ち時間はAEの公開仕様に合わせて8秒、変更可能。停止中キャッシュだけを無効にできる。
- 保存済み・通常描画が必要なフレームを飛ばす。入力、編集、シーク、再生、ビュー変更、消去で中断/再開する。古いworkerが編集後の巡回位置を上書きしない。
- アイドル保存の直前に中断と世代を再検査し、GPU capture中の消去や取消で保存しない。保存拒否を成功として数えない。
- ツールにRAMの現在の予算、設定上限、書き込み待ちの保持量を表示する。

256MiBは1920×1080 BGRAの約32フレーム、2GiBは約258フレーム（30fpsなら約8.6秒）に相当する。
これはRAM画素ストアだけの概算。GPU、借用中の配列、書き込みキュー、YMM4本体、デコーダの使用量は別に存在する。
縮小直後にプロセスの使用量が即座に下がることは保証しない。GCの強制実行はしない。
ディスクは現状4GiB。全範囲を巡回できることと、全フレームが同時に容量内に収まることは別で、LRU退避は続く。
デコード未完了などで保存できなかったフレームの無期限再試行は行わず、変更・シーク等で次の巡回が始まる。

## 検証

LinuxでStoreChecksHarnessとReadinessChecksを実行。既存のストア/時刻/キー/選択枠/帯/契約テストに加えて、
RAM縮小・復元・借用中の画素、ディスク維持と再起動、縮小後の破損検査、並行read/write/resize、
メモリ不足と回復、全順序の境界と重複なしを検査する。
FileLeaseChecksはWindowsのファイルIDを使うため、Windowsで実行する。

`.github/workflows/cache-development.yml` はこのブランチへのpushとPRで動作する。
公式更新手順とハッシュ照合で4.56.1.0を取得し、Windowsでビルド、native invariants、ストア、readiness、
file lease、実ホストの依存キー、WARPの画素一致とディスク供給を検査する。
RAMをゼロまで縮小→復元したプレビューのディスク供給、古い静止ビューのprime、capture中の取消/消去も含む。
YMM4のバイナリはコミットしない。実機RTX3060の速度と実GUI操作はこのCIでは検証しない。

実行結果: [YMM4-dlls Windows検証](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36854490341) は成功。
固定release 0.1でnative invariants、プラグインのビルド、StoreChecksHarness、ReadinessChecks、
FileLeaseChecks、CacheChecks、HostCacheProbe --integration/--gpu（WARP、動画fixture付き）を通過した。
Linuxでも4.56.1.0の実DLLに対してプラグインとHostCacheProbeをクロスビルドし、警告・エラー0。
RAM回復時にホストを再描画せずディスク画素を供給すること、古い静止ビューのprime、capture中の取消/消去も成功した。

## 次に埋める差

| AEの挙動 | 現状と次の作業 |
|---|---|
| 重い未保存フレームを待ち、音声も同期する | 未実装。音声時計を含む取消可能なbuffering stateを設計。StopAsyncで0へseekする既存APIをそのまま流用しない |
| Cache Before Playback | 未実装。上記schedulerで連続区間を確保してから再生する |
| 再生中の保存コストを抑える | 復元済み画像のGPU保持を追加（GPU_FRAME_RETENTION.md）。cold renderのCPU readbackは残る。既存の焼き込みtarget保持と遅延降格を次に検証する |
| 無損失圧縮ディスクキャッシュ | 未実装。読込速度と圧縮率を実素材で測ってから採用する |
| 素材・レイヤー・エフェクト段の再利用 | 合成済みフレーム中心。receipt/dependency graphと段単位キャッシュは後段 |
| MFR | 未実装。所有device/contextを共有しない描画workerとホスト互換契約が必要 |

## AEの一次資料

ユーザー指定の `sabiasagimp4-ai/aesdk` の main (`390a34d0`) にある
`AE_ComputeCacheSuite.h` と `AE_CacheOnLoadSuite.h` を確認した。
Compute Cacheは入力の状態をキーに含め、計算済み値のcheckout/checkinで寿命を管理し、
計算中は呼び出し側が待機か即時missを選ぶ契約。RAM退避後も借用配列を変更しない今回の処理は
この寿命の要件に沿う。ただし計算のsingle-flightや段単位のreceiptは未実装で、
SDKの仕組みだけではAE本体の再生・音声時計を再現できない。
Cache On Loadはプラグインの起動時ロードに関するAPIであり、フレームのディスク供給APIではない。
SDKのコードは転載せず、契約の確認に利用した。

`sabiasagimp4-ai/YMM4-dlls` の main/ci/nvenc-verify/ci/gui-smoke/ci/host-versions/ci/ymm4-watch を確認。
実ホスト検証は同リポジトリのrelease 0.1と既存verify手順も利用する。
`tools/ci/cache-development-release.yml` を独立した `ci/ae-cache-continuation` へソースと共にミラーし、
既存CIブランチを上書きせずWindowsで検査する。zipのSHA256は
`49c0ed689f545737b7ce939971bfc625962e00791c57883dc8e6f058aa336c5a` に固定する。

- [Previewing](https://helpx.adobe.com/after-effects/desktop/view-and-preview/preview-video-and-audio/previewing.html): Cache Before Playback、Preview from Disk Cache。
- [Multi-Frame Rendering](https://helpx.adobe.com/after-effects/desktop/render-and-export/multi-frame-rendering/multi-frame-rendering.html): 停止中の自動レンダリング、8秒の既定待ち時間、CTIに対する順序と対象範囲。
- [Lossless Compressed Playback](https://helpx.adobe.com/after-effects/desktop/view-and-preview/preview-video-and-audio/lossless-compressed-playback.html): 現行の無損失圧縮ディスクキャッシュ。

AEは公開された利用者向け挙動を目標にする。内部実装の完全な一致や、機能全体の完成はこの変更では主張しない。
