# GPU保持による復元高速化の実測

ソース `a91e1c2c7e6fe29358ef97d196275687d9c013f1`。
YMM4 4.56.1.0、Windows 2022、Microsoft Basic Render Driver/WARP。
RTX3060、GPU実行時間、Present/音声同期、NVENCの速度は測定していない。

- [全ホスト検証・比較測定・パッケージ成功](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36875040153)
- [実GUIの巡回・ログ採取成功](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36875046663)
- [ソース側portable/Windows CI成功](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/36875041175)
- 設計は [GPU_FRAME_RETENTION.md](GPU_FRAME_RETENTION.md)。生ログとJSON報告は [traces/2026-10-01-gpu](traces/2026-10-01-gpu)。

## 同じ8フレームの反復

1080p、shape＋text、8枚を100回表示するsource-only試験。
ウォームアップは測定外。計測はTimelineSource.Update＋stand-in DrawのCPU wall time。
詳細traceをOFFにし、RAM/GPU各3passをRAM→GPU→GPU→RAM→RAM→GPUの順で比較した。
各passの100フレーム平均を示す。GPU再利用には各passで100 GPU hit、RAM再利用には100 RAM hitを確認。

| pass | 経路 | ms/枚 |
| --- | --- | ---: |
| 0 | RAM | 6.480 |
| 1 | GPU | 1.566 |
| 2 | GPU | 1.985 |
| 3 | RAM | 9.828 |
| 4 | RAM | 8.484 |
| 5 | GPU | 1.642 |
| 3pass平均 | RAM | 8.264 |
| 3pass平均 | GPU | 1.731 |

平均の比は4.774倍、時間は約79%減。このworking set/fixtureにおけるRAM復元との比較であり、
YMM4全体やRTX3060の速度を表さない。各3passのばらつきがあるため母集団の保証値や信頼区間にしない。
従来の100枚巡回も残しているが、そのRAM経路6.824ms/枚と上記8枚反復を直接比較しない。
cache OFFの100枚巡回は1.157ms/枚、cold-storeは12.314ms/枚。この軽いfixtureではcache OFFも速い。
初回レンダー・readbackの費用までなくなったわけではない。

別の詳細trace ON試験では100 GPU hit、1,400件、drop=0、OpenSpans=0。
CopyFromMemory、bitmap確保、RAM検索のspanは0。8枚のGPU結果と通常ホスト描画の画素一致を確認。
100枚の従来RAM画素一致、borrow保持のまま全eviction、viewport変更、モデル編集、purge、
source破棄後の全画像予算返却も成功した。
編集/purge直後は背景依存検査が未準備の場合があるため、破棄fixtureはGPU保持ができるまで測定外で準備する。

## 長い巡回への追従

128MiBに収まる枚数を越える巡回では、純粋なLRUは再訪直前に追い出してしまう。
利用頻度で追加を判断し、同頻度なら既存entryを残し、512観測ごとに頻度を減衰させた。
予算の増量や依存検証の省略をせずに対応する。

同じGUI操作scriptの独立実行を比べると、GPU hit全体は
[LRUのみ](https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36872282011)の1から、頻度による追加で35になった。
これは経路の観測比較であり、run間のCPU速度・全体FPSの統制比較ではない。
新しい実行は20,185件、drop=0、OpenSpans=0。2回目再生のPlaying Updateの内訳:

| 経路 | n | 平均ms | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: |
| GPU | 34 | 2.705 | 2.762 | 3.140 |
| RAM | 57 | 3.483 | 3.355 | 4.512 |
| 同じ出力の再利用 | 10 | 1.483 | 1.511 | 1.957 |

残るRAM経路では転送が発生する。予算外の全フレームがGPU再利用できるという主張はしない。

## 次の下限を作っている費用

GPU-hit traceのroot Update平均1.836msに対し、キー生成0.829ms、状態再検証は1回0.452ms（2回/枚）。
再検証の子のcapture依存確認は1回0.417ms。親子を加算しない。
COM参照のborrowは0.010ms。コピーを省いた後は依存検証が主要な調査対象になった。

次に調べるのは、ファイル/ディレクトリの同一性を保証したまま検証を安くする方法、
レンダー時に既に焼き込んだGPU画像の保持、adapterの実予算に追従する保持量である。
現状は128MiBの固定payload予算。任意の効果graphの参照保持や、検証回数を減らすだけの変更は正しさを保証しない。
