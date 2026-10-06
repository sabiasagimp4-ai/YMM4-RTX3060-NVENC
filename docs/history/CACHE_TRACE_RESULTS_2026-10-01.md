# 実YMM4の観測結果と次の実装判断

GPU保持の導入前に取得した観測記録。対象commitと測定値は当時のものを保持する。
この測定から派生したGPU保持は実装済みで、現在の結果は [GPU_FRAME_RETENTION_RESULTS_2026-10-01.md](GPU_FRAME_RETENTION_RESULTS_2026-10-01.md)、残る課題は [AE_CACHE_DEVELOPMENT.md](../AE_CACHE_DEVELOPMENT.md) を参照。

## 測定条件と保存先

YMM4 4.56.1.0、Windows 2022 GitHub-hosted runner、Microsoft Basic Render Driver/WARP。
ユーザーのRTX3060実機での結果ではない。素材はCIの使い捨てプロジェクト。
CPU wall timeをStopwatchの生ticksで採取。GPU実行時間、Present、音声同期は未計測。

- [実GUI操作・ログ採取成功](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36861076412)
- [ホスト・画素一致・詳細ログ・パッケージ検証成功](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36861078427)
- 対応するソース: `87eabcf2faa273156ec08dec4393f7dcbbe54959`。
- raw JSONL・スクリーンショット・診断版 `.ymme` は上記Actions artifacts。保存期間14日。
  JSONLは [traces/2026-10-01](traces/2026-10-01) にgzipで保存し、Actions期限後も再集計できる。
  圧縮前後のSHA256・実行run・ソースcommitはmanifest.jsonに記録。スクリーンショットとパッケージはActions artifacts。

## GUIで確認できたこと

14,728件を受理・書込、drop=0、OpenSpans=0、footerあり。
8種のランタイム処理クラスで4,833回のprocessor-callを観測。
フック成功266メソッドは「呼ばれた266種類」を意味しない。

| 実行された処理クラス | 呼出し数 |
| --- | ---: |
| DrawingEffect | 1,245 |
| ShapeSource | 731 |
| SolidColorBrushSource | 730 |
| BrushWrapperSource | 730 |
| GeometryShapeParameterSource | 730 |
| TextSource | 514 |
| BloomEffectProcessor | 152 |
| NumberTextSource | 1 |

root Updateの経路: render=564、live=33、bypass=22、ram=187。
このGUI試行ではdisk経路なし。別の実ホスト試験でdisk=73を観測。
対象外理由は編集中5、外部素材検証準備中17。未知の効果を安全と判定したことにはならない。

idle-fill、paused-seek、playback-1、playback-2、edit-delete、undo、redo、preview-wheelの
マーカーはすべて記録できた。マーカーは操作意図であり、全編集操作が成功した証明ではない。
特に1件程度の編集後Updateから無効化の一般的な正しさを判断しない。

集計をoperation IDで経路と関連付けると、再生マーカー内のPaused更新を分離できる。
以下は**Playingのroot Updateのみ**。旧集計の操作区間全体の平均と混ぜない。
p値はretained samplesのnearest-rank。

| 操作 | 経路 | n | 平均ms | p50 ms | p95 ms | 最大ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| playback-1 | RAM | 90 | 3.829 | 3.282 | 5.007 | 20.025 |
| playback-1 | live | 14 | 1.638 | 1.652 | 1.853 | 1.853 |
| playback-2 | RAM | 92 | 3.177 | 3.136 | 4.059 | 4.222 |
| playback-2 | live | 9 | 1.678 | 1.562 | 1.986 | 1.986 |

フック失敗5件は同じ1メソッド:
`AudioVolumeVideoEffectProcessorBase.Update`、MVID `ac765de8-d44f-44f1-a094-961becf4d22e`、
metadata token `100677283`、ArgumentException。失敗件数と未観測メソッド数を区別する。
この処理の実行時間は今回のcoverageからは保証できない。

## ログから派生させた実装

1080p/100フレームのsource-onlyホスト試験では、RAM検索CacheRead平均0.008msに対し、
CacheRestore平均6.582ms。復元には状態検証も含まれているので、これだけではGPU転送の問題と決められない。
そこで復元を次の観測点に分解した:

- 状態・世代検証、モデル/親/ファイルlease検証、描画環境/viewportキー再検証。
- bitmap確保、CopyFromMemory呼出し、command recording。
- 出力ロックの待ち、出力差し替え/旧出力破棄/状態登録。

依存確認を省略して速くする変更は行っていない。追加spanのframe/operation/parentと包含関係を
100フレームで検証するホスト試験を追加した。
集計もUsage・実際の経路で分割し、対象外理由とフック失敗のメソッド別内訳を出すようにした。

OFF/cold/RAM/trace-ONを固定順に各1回測った結果だけでは、計測オーバーヘッドや性能差は推定しない。
100フレーム画素一致はこのfixtureの結果であり、全プラグイン対応の根拠にはならない。
動的な対応範囲を広げる際は、新しい処理の観測と依存/完成状態の確認を組み合わせる。

## 復元内訳を追加した再検証

[Windows実ホスト検証成功](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36865230266)。
ソース `45746b1a7ae65311f73c0069216fbf4e9057a725`。
100フレーム1080pのRAM経路で1,952件を受理・書込、drop=0、OpenSpans=0。
追加した5区間を各100件採取し、operation IDと親CacheRestore内の時間包含を検証。
100フレーム画素一致、GPU予約解放、既存のnative/host/store/readiness/file lease試験も成功。

| 観測点 | n | 平均ms | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: |
| root Update | 100 | 8.665 | 8.221 | 10.813 |
| CacheRestore全体 | 100 | 6.999 | 6.654 | 8.948 |
| CopyFromMemory呼出し | 100 | 5.375 | 5.063 | 7.147 |
| 状態再検証 | 200 | 0.691 | 0.638 | 0.934 |
| うちcapture依存検証 | 200 | 0.654 | 0.613 | 0.891 |
| bitmap確保 | 100 | 0.145 | 0.118 | 0.157 |
| command recording | 100 | 0.048 | 0.040 | 0.066 |
| 出力差し替え・旧出力破棄 | 100 | 0.022 | 0.020 | 0.030 |
| RAM検索 | 100 | 0.012 | 0.013 | 0.015 |
| 出力ロック待ち | 100 | 0.000435 | 0.000400 | 0.000700 |

状態再検証は復元前と差し替え直前の2回なのでn=200。capture依存検証はその子であり、
2行を加算してはいけない。CopyFromMemoryはWARP上のCPU呼出し時間で、GPU実行時間ではない。

このfixtureではbitmap再利用だけより、繰り返すCPU→描画資源コピーを避けるGPU側の保持が
次の有力な調査対象になった。実装には予算・device lifetime・viewport・依存revision・borrow lifetimeを
同時に扱う必要がある。今回のデータだけでRTX3060にも同じ順位を適用せず、検証や転送を省略しない。


保存済みログの再集計例（Pythonのみで展開可能）:

```sh
python -c "import gzip,pathlib; p=pathlib.Path('docs/history/traces/2026-10-01/performance-trace.jsonl.gz'); pathlib.Path('performance-trace.jsonl').write_bytes(gzip.decompress(p.read_bytes()))"
python tools/analyze-cache-trace.py performance-trace.jsonl --output performance-summary.json
```
