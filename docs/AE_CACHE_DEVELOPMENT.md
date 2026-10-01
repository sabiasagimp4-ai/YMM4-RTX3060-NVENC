# AEキャッシュに近づける開発方針

2026-10-01更新。目的はYMM4の標準プレビューと動画出力で、完成フレームを蓄積・供給し、編集・Undo・素材更新・取消後も正しい画像を表示すること。AEの公開された挙動とSDK契約を参考にする。内部実装の完全一致は確認できていない。

## 現在の基準

全4ブランチの内容を `main` へ統合済み。PR #1・#2はマージ済みで、旧開発ブランチ固有の未統合コミットはない。以後の作業はmainから始める。古い引継ぎ資料と統合前の作業指示は削除し、現行仕様を [CACHE_BEHAVIOR.md](CACHE_BEHAVIOR.md) に集約した。

統合実装の [main CI](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/36878854622) は成功（`970debc7da66b537871035cb3088e657b5fa792d`）。`cache-development` はmainへのpush・対象ファイルのPR・手動実行で動作する。YMM4バイナリはコミットしない。

動的計算・依存provider・費用判断・圧縮・指定範囲の [Windows／portable CI](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/36891225375) も成功（`741df2af59dd8461c02f1804b3c969f3e3c2d1ca`）。実4.56.1.0のprovider発見・隠れた状態の変更、従来のdecoder／編集／Undo／再起動／WARP画素一致・GPU借用寿命を確認した。RTX 3060やAE 26.3での実機性能を測った結果ではない。

## 実装済みの範囲

| 領域 | 現状 |
| --- | --- |
| 保存・供給 | 通常プレビュー／出力の完成画素をRAM・ディスクへ保存。再生read-aheadと停止時の読込待機 |
| 完成判定 | 動画ソースの実状態と要求時刻を照合し、未完成decodeを保存しない |
| 依存キー | 正確なticks、フレーム単位のアイテム・素材、編集／Undo、usage／viewport／描画環境 |
| RAM | 背景samplingで予算を調整。縮小・回復・LRU・借用寿命・ディスク維持 |
| idle | 既定8秒後、指定範囲（既定は全体）を3順序で巡回。操作・変更・purgeで取消 |
| GPU保持 | 復元した不変画像を保持し、反復時の再転送を省く。boundedな利用頻度履歴で追加を判断 |
| 外部処理 | アイテム単位のbypass、固定MVIDのCommunity監査、個別の信頼設定 |
| 観測 | Processor／Sourceの実装を動的に発見し、後読み込みに追従。経路・内訳・親子・因果ID・欠落を記録 |
| ホスト互換性 | 実バイナリの前提コードを機能ごとに照合。新しい版を定期CIで確認 |

動的な観測・メモリ配分・利用履歴への追従と、未知の処理の安全性判定を混同しない。観測したという理由だけでキャッシュ可能へ昇格しない。詳細は [診断](CACHE_DIAGNOSTICS.md)・[ホスト契約](HOST_CONTRACTS.md)。

任意の計算共有、provider依存、要求充足契約、CPU費用に基づく採用、Brotli／raw保存、指定範囲、読込費用による先読み量を追加した。[動的キャッシュAPI](DYNAMIC_CACHE_API.md) に実装と限界を記す。汎用計算のSingleFlightをホストの描画全体へ接続したわけではない。

## 次の課題

2026-10-02のSDK再調査で、計算値の登録、cached-only lookup、時間範囲の依存、先読みの二段階価値判断にも差を確認した。[AE SDKとの契約差](AE_CACHE_CONTRACTS.md) を参照。下表の速度最適化と並行して、元の描画費用と復元費用の記録・採用判断、および外部処理が依存を報告する契約を優先する。未実装の再生schedulerを完成扱いにしない。

| 順序 | 課題 | 実装前の条件・完了判断 |
| --- | --- | --- |
| 1 | GPU hitでも残る依存検証・キー生成を軽くする | ファイル／ディレクトリ同一性、lease、採用直前検証を維持し、費用を分解して比較する |
| 2 | 初回renderのreadbackを抑える | 焼き込み画像の安全な取得位置、device／context、GPU完了、予算、寿命を実証する |
| 3 | 音声と同期したレンダー待機・Cache Before Playback | 映像だけ止めず音声時計も調停。停止・seek・編集・repeat・末尾・disposeで取消可能にする |
| 4 | whole-project JSONの差分記述 | item、animation、effect list、nested parameter、character、reader／global設定の変更を漏らさず、移行中は旧方式と照合する |
| 5 | ホスト描画のSingleFlight | 完全な出力キーとcontext互換条件が同じ計算だけ共有。consumer取消とjob取消を分け、idle／live／exportの重複と待機残留を検証する |
| 6 | 無損失圧縮の実素材性能 | Brotli／raw fallbackと再起動後の往復は実装済み。実素材で圧縮率・読込速度・CPU費用を比較し、採用を調整する |
| 7 | canonical raster・item／effect段の再利用 | viewport前の取得位置、補間・pixel位相・clip・透明度・text AA・formatの画素一致を検証する |
| 8 | 履歴依存処理・MFR | 依存グラフ、開始状態／checkpoint、編集の伝搬、独立device／contextとホスト契約が必要 |

`TimelineAudioPlayer.Position` が再生時計。`StopAsync()` はゼロへseekし、`EndAudioTask()` はrepeat時に開始位置へ戻すため、そのままbuffer待機へ流用しない。UI／render workerでasync toggleを同期Waitしない。

D2D `Map(Read)` にDoNotWaitはなく、1フレーム遅延やreadbackリングだけで非ブロックとは言えない。D3D11 query等を使う場合もD2D flushとの順序とdevice一致を確認する。現在のGPU保持は復元画像が対象であり、cold renderのreadbackは残る。

SingleFlightではidleが不要になってもlive／exportのjobを巻き添えにしない。保存価値と画像の有効性を分け、容量不足だけで要求された有効結果を捨てない。採用直前の依存・取消・世代・予算確認から公開まで既存guardを使う。同じTimelineSourceや描画contextを複数Taskから同時UpdateするだけのMFRは行わない。

## 検証の基準

同じ要求条件の画素一致を先に守り、OFF／cold／RAM／disk／GPU／live／bypassを分ける。編集→Undo、素材置換、viewport、選択枠、decoder失敗、purge、退避中のborrow、破棄後の資源返却を確認する。計算共有を追加した場合は実render数・join・取消も測る。

性能は対象commit、素材、解像度、adapter、warmup、pass順序、サンプル数、p50／p95、drop／coverageを記録する。親子spanを加算せず、Update／DrawのCPU経過時間をPresent・音声・GPU実行時間や全体FPSとして扱わない。詳細traceのON／OFF比較も別に行う。

- [実ホストprobeと実行コマンド](../tests/HostCacheProbe/README.md)
- [動的計測の実測と生ログ](CACHE_TRACE_RESULTS_2026-10-01.md)
- [GPU保持の実測と生ログ](GPU_FRAME_RETENTION_RESULTS_2026-10-01.md)
- [統合前のRTX 3060 source-only測定](preview-performance-rtx3060.json): `e60c4552` 時点、100フレーム、1080p、shape＋Arial、disk無効。GUI／audio／Present／pacingを含まない歴史的データ

ユーザーのRTX 3060上の最新ビルドでのGUI・実プロジェクト速度・長時間NVENC出力は未確認。VFR、実device loss、実disk full、UI操作から表示までのp50／p95・frame dropの検証も残る。CI成功や過去のNVENC smokeをこれらの代用にしない。

## AEの参照

`sabiasagimp4-ai/aesdk` の [固定版AE_ComputeCacheSuite.h](https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_ComputeCacheSuite.h) を確認した。入力状態をキーに含め、checkout／checkinで借用寿命を管理し、計算中は待機または即時missを選ぶ契約を参考にする。段単位receipt・計算single-flightはまだ実装していない。

`AE_CacheOnLoadSuite.h` は起動時のプラグインロードのAPIで、フレームのディスク供給APIではない。SDKは契約確認に使い、コードを転載しない。SDKだけではAE本体の再生・音声時計を再現できない。

- [Previewing](https://helpx.adobe.com/after-effects/desktop/view-and-preview/preview-video-and-audio/previewing.html)
- [Multi-Frame Rendering](https://helpx.adobe.com/after-effects/desktop/render-and-export/multi-frame-rendering/multi-frame-rendering.html)
- [Lossless Compressed Playback](https://helpx.adobe.com/after-effects/desktop/view-and-preview/preview-video-and-audio/lossless-compressed-playback.html)

`YMM4-dlls` は実ホスト・GUI検証用。`tools/ci/cache-development-release.yml` と `cache-diagnostics-gui.yml` は同リポジトリへミラーするCIテンプレートで、ソースrepoのactive workflowとは区別する。
