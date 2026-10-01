# 実行ログから対応範囲を広げる

2026-10-01。ユーザー提案の「実YMM4を操作して詳細ログを取り、挙動に合わせて派生する」を開発の土台にする。
プラグイン名の一覧を増やすだけでは、新規・更新・後読み込みの処理に追従しない。
動的な**観測**を実装している。未検証の処理をキャッシュ可能へ自動昇格するものではない。

## 採取

ツール「描画キャッシュ」で「詳細ログを開始」。右の入力欄に操作名を記入・変更すると区間マーカーを残す。
「詳細ログを停止」でフックを外し、書込完了を待つ。保存先は画面に表示する。
記録は既定OFF。既存ログは上書きしない。開始/停止はキャッシュ設定と独立。
自動操作では `YMM4_CACHE_TRACE` に新しいJSONLの絶対パス、`YMM4_CACHE_SCENARIO` に開始時の操作名を指定してYMM4を起動する。
`CacheDiagnostics.Start/MarkScenario/StopAsync` は外部ツール用のpublic API。

`tools/ci/cache-diagnostics-gui.yml` は `YMM4-dlls` の独立した `ci/cache-diagnostics-gui` に置いて実GUIを起動する。
生成した使い捨てプロジェクトで、停止中の巡回、paused seek、2回の再生、Delete、Undo、Redo、preview wheelを試行し、
終了前にツールの停止ボタンから書込完了を確認する。操作名は意図であり、入力が必ずその編集・ズームを生んだ証明ではない。
ログのフレーム時刻・経路・viewport/描画・coverageと照合し、確認できた挙動だけを結果とする。
ホストのドッキング枠がUIAを隠す場合は、配置したCI用ツール窓を基準に入力/停止を操作する。実機の窓へ座標操作する仕組みではない。
既存の実ホストWARP検証も `--trace-output <path>` で採取できる。

## 記録内容

- セッション: UTCとStopwatchの原点、実際の周波数、OS/runtime、process ID、論理CPU数。
- 各span: 生の開始/終了ticks、span ID、親ID、operation ID、managed thread ID、frame time ticks、Playing/Paused/Exporting、stage/category/component、outcome。
- Update経路: render/live/GPU/RAM/disk/bypass/exception。対象外の理由、decoderのready/not-readyも別イベント。
- キー生成、CacheRead（RAM/ディスク検索）、CacheRestore（状態検証を含む画像復元）、lookup全体、ホストUpdate、Draw、GPUコピー提出、配列確保、Map待ち、memcpy、RAM登録、ディスク投入。
- 復元内訳: `cache-state-validation` の下に `capture-dependency-validation`（モデルrevision、親、ファイルlease検証）と
  `render-environment-key-validation`（現在の描画環境・viewportのキー再検証）。復元前と出力差し替え直前の双方を測る。
  `restore-bitmap-allocation`、`restore-copy-from-memory`、`restore-command-recording`、`cache-output-lock-wait`、
  `cache-output-commit`（出力差し替え・旧出力破棄・状態登録）を別spanとして記録する。
  CopyFromMemory/command recordingはネイティブ呼出しのCPU wall time。GPU完了を意味しない。
- GPU保持のhitでは転送・bitmap確保・RAM検索を省略する。経路を混ぜず、実行されたspanだけを集計する。
  設計と最新の測定は [GPU_FRAME_RETENTION.md](GPU_FRAME_RETENTION.md)・[GPU_FRAME_RETENTION_RESULTS_2026-10-01.md](GPU_FRAME_RETENTION_RESULTS_2026-10-01.md)。
- ディスクworker: 読み書き処理のwall timeとqueue滞在時間。投入元のoperation IDで関連づける。
- 遅延readback: 次のフレームで完了しても、元のframe time/operation IDに帰属させる。
- Processor/SourceインターフェースのUpdate/Draw/Read/GetFrame/GetFrameAsyncを実装するクラスをロード済みアセンブリから動的に発見する。
  1秒ごとの背景検査で後読み込みにも追従。型名・method・MVID・metadata tokenとhook成功/失敗を記録する。
  既存detourとreadiness所有のdecoder/timelineには追加のobserverを入れない。decoderは既存完成判定フック内で計測する。

描画スレッドでファイルI/OとJSON変換をしない。8192件の有限queueへ非待機で投入し、専用writerが書く。
混雑時は記録を落として件数をfooterへ残す。終了時にspanが進行中ならOpenSpansも残す。
欠落・進行中span・footerなし・読込途中を「完全な測定」として扱わない。

## 時間の意味と限界

CPUの**経過時間**であり、CPU使用時間/cycle数ではない。OS schedulingやlock待ちも含む。
spanはinclusive。親と子を足してフレーム時間にしない。TotalUpdateとDrawの和もPresent・音声・再生時計の全体ではない。
MapWaitは同期待ちを含むがGPU実行時間ではなく、BeginGpuCopyは提出側CPU時間。
非同期methodはTaskを返すまでのasync-submitだけであり、Task完了時間ではない。
独立したworkerへの因果IDは明示的に伝えたディスク投入等だけ保証する。任意のTask/独自threadへの自動伝播は保証しない。
true GPU durationにはtimestamp/disjoint query、再生・Present・音声の全体には追加の観測点が必要。

動的detourには導入コストと計測コストがある。記録ON/OFFで同じ素材・同じ操作を繰り返し、warmupとcoldを分ける。
1回の平均で判断せず、retained samplesのp50/p95/p99、最大、サンプル数とdrop/coverageを併記する。
一度同じ絵が出たことから、乱数・前フレーム・隠れたファイル依存がないと結論しない。
frame時間はソース時刻であり、再生時計や音声の基準時刻を代用しない。

## 集計

```sh
python tools/analyze-cache-trace.py trace.jsonl --output trace-summary.json
```

raw traceを温存して全retained samplesから集計する。CPU spanの加算によるランキングはしない。
stage/component別のサンプル数・平均・p50/p95/p99/最大、操作区間、Usage、経路、coverageを出す。
root Updateが後で書かれてもoperation IDで子・ディスクworkerを実際の経路へ関連付ける。rootのないspanはunattributed。
Playing/Paused/ExportingとRAM/live/renderを混ぜない。対象外理由とcoverageのメソッド別重複件数も出す。
観測記録は [CACHE_TRACE_RESULTS_2026-10-01.md](CACHE_TRACE_RESULTS_2026-10-01.md) を参照。
既定100万spanの読込上限、破損行、drop、OpenSpans、footerなしはComplete=false。

次の実装判断は、(1)どの処理・時刻が対象外か、(2)未完成decodeや他の待ちが何回出るか、
(3)readback/キー/描画/ディスクのどこが占めるか、(4)操作後に古い画素を使っていないか、を照合して行う。
観測結果による先読み量・描画batch等の適応と、処理側が完全な依存/完成状態を提供する契約APIを別々に進める。
RTX3060実機の結果とWARP CIの結果は混ぜない。

`compute-cache` は登録classの計算、`frame-cost` はrender／restore別のUpdate+Draw ticksと周波数、`cache-admission` はreadback開始前の見送り、`disk-compression`／`disk-decompression` はworkerのcodec CPU経過時間を示す。GPU実行時間ではない。動的providerの安全条件と費用判定は [DYNAMIC_CACHE_API.md](DYNAMIC_CACHE_API.md)。
