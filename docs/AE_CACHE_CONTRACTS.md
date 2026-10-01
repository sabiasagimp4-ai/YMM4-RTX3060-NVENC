# AE SDKと現行キャッシュの契約差

2026-10-02 JSTに再調査。SDKは `aesdk` の固定コミット `390a34d0cedba002814bd1879ec4c604bff46bc2`（26.5）、比較したYMM4実装はmainの `fad2d489513f069576ecc13f5ff3a35f8b5494a1`。AE本体はこの環境では実行していない。ヘッダーの契約、Adobe公開仕様、ソース比較からの判断を区別する。

SDKにAE本体のキャッシュ実装は含まれない。公開契約への一致と、未公開アルゴリズムの一致は別の目標。AE 26.3のUI・待機・退避タイミングは実測が必要で、26.5 SDKから断定しない。

この表は上記固定commit時点の監査。以後の計算共有・provider・費用判断・圧縮・指定範囲の実装状況は [動的キャッシュAPI](DYNAMIC_CACHE_API.md) を参照。表の未実装項目をすべて解消したわけではない。

## SDKで確認できる差

| 契約 | SDKの根拠 | 現行実装と差 |
| --- | --- | --- |
| 任意の計算クラスの登録 | [S1] ClassRegister、generate_key／compute／approx_size_value／delete_compute_value | 合成済み画素中心。動的Processor観測と、任意の計算結果を登録・共有する仕組みは別。計算クラスの登録APIはない |
| レンダーなしで入力状態を識別 | [S1] generate_key、[S2] GetCurrentState | SHA-256の状態キーはあるが、whole-project JSONの記述・分解とファイルleaseが必要。AEのホスト提供入力tokenに相当する供給元がない |
| 読取専用lookupと計算要求を区別 | [S1] CheckoutCached、ComputeIfNeededAndCheckout | GetResidencyは状態確認だけ。TryGetはdisk読込を予約し得る。即時lookup、I/O予約、任意の計算開始を共通APIとして区別していない |
| 同じ計算中ジョブを共有 | [S1] wait_for_other_threadB、[S3] SUPPORTS_THREADED_RENDERINGの説明 | disk read／writeの重複予約は抑制するがrenderのSingleFlightはない。複数要求で同じ計算を共有するreceipt／Pending状態がない |
| 借用・解放・値の総費用を管理 | [S1] receipt、approx_size_value、delete_compute_value | RAMは不変配列、GPUは独立COM参照でborrowを保護。任意の非平坦な計算値を統一して数える／破棄する契約はない。payload予算は実VRAM全量ではない |
| 時間方向の依存追跡 | [S2] 時間範囲の状態比較、[S3] AUTOMATIC_WIDE_TIME_INPUT | アイテムの表示区間・transition直前・wide依存を追うが、効果が実際にcheckoutした別時刻／入力の記録はない。scene・音声波形はwhole-project依存へ落とす |
| 隠れた状態のGUIDへの混入 | [S3] I_MIX_GUID_DEPENDENCIES、GuidMixInPtr | フォント・辞書・reader・MVID等は組込み知識で列挙。外部効果自身が追加依存を報告する契約がなく、個別信頼に依存する。無効な効果の非影響状態を除外する一般規則もない |
| 画像の有効性と要求充足を分ける | [S3] RenderRequest／PreRender、[S4] IsRenderedFrameSufficient | 完全なviewportキーの一致で復元する。rect・channel mask・bitdepth等の要求と、保存画像の充足判定を汎用化していない。別viewportへの流用は未実装 |
| レイヤー／効果途中の再利用 | [S4] CanvasSuite8、num_effects、CheckRenderReceipt | 合成後のframe cache中心。効果prefixや素材・レイヤー単位のreceiptと中間画像は保存しない |
| 先読みの価値を開始前・採用前に判断 | [S4] IsItemWorthwhileToRender、HasItemChangedSinceTimestamp、CheckinRenderedFrame | 完成・世代・依存・保存済み確認はある。元の描画費用をレコードへ保存せず、RAM／diskの採用価値に使わない。GPUの利用頻度による追加判断とは異なる |
| 非同期要求の置換・取消 | [S4] RenderAndCheckoutLayerFrame_Async、CancelAsyncRequest、AsyncManagerのpurpose_id | idleジョブ取消と世代保護はある。live要求・disk delivery・render・音声を統一したrequest brokerはない |
| 並列描画の明示的な宣言と状態分離 | [S3] SUPPORTS_THREADED_RENDERING、ConstSequenceData、[S5] GPU exclusive access | MFR未実装。設定の信頼はthread safetyの宣言ではなく、動的observerもそれを証明しない |

### 読み違えやすい点

**Compute Cacheのno-waitは非同期実行スイッチではない。** キャッシュが存在しないと、FALSEでも呼出し側がcomputeしてcheckoutする。FALSEで即時Pendingを返すのは、別スレッドが既に計算している場合。UIで常に即時returnしたい用途にはCheckoutCachedか独立した非同期schedulerが必要。[S1]

複数の値が必要なら、先に他スレッド待機なしのcheckoutを複数行い、Pendingだった値だけ後で待つパターンが示される。これは依存DAG、描画context互換性、consumer取消とは別の契約で、Task.Runを並べるだけでは一致しない。

receiptは少なくとも3種類ある。Compute Cacheのcheckout receiptは計算値の借用、RenderSuiteのframe receiptはread-only worldの借用、CanvasSuiteのrender receiptはレイヤー／効果prefix等の描画状態を照合するもの。CanvasのVALID_BUT_INCOMPLETEをdecoder timeoutや計算Pendingと同一視しない。[S1][S4]

GetCurrentStateは入力状態を返し、画素hashのためのrenderを要求しない。ただしUPDATE_PARAMS_UI内ではdeadlock回避のためrandom stateとなる例外がある。「AEのstate hashならUIで常に安定」と解釈しない。[S2]

CheckinRenderedFrameのticks_to_renderは60 Hzの近似描画費用。Stopwatch ticksとは単位が異なる。元のrender費用をhit時の復元時間で上書きしない。[S4]

AsyncManagerはcustom UI向けの契約で、AE標準previewがそのAPIを使っている証拠ではない。非同期完了callbackには取消・error・receiptが渡るが、ここからcallbackのUI thread実行を仮定しない。[S4]

GPU suiteのexclusive access・allocation／free／purgeはGPUプラグイン向けの契約で、AE全体のallocatorの実装までは示さない。[S5]

HashSuiteはhash生成／混入の入口を公開するが、内部算法や全依存構成は公開していない。GUIDの見た目に合わせてSHA-256を短くしても契約一致にはならない。[S6]

CacheOnLoadSuiteは起動時のプラグインロードの扱いであり、フレームdisk cacheや動的Processor検出のAPIではない。[S7]

## 再利用範囲の具体的な差

`FrameDependencyIndex.Entry.Hash` はアイテム状態の記述をhash化し、同じアイテム集合の区間で共有する。キーフレームを変えると、評価結果が変わらない時刻でも、そのアイテムを含む区間の入力hashが変わり得る。フレーム単位キーは実装済みだが、時間依存checkoutに相当する最小範囲の無効化とは一致していない。

`TimelineFrameCache.MakeKey` は正確な時刻とviewport内のScene／Timeline IDを含む。同じ絵となる別時刻や複製timelineでも一般的な内容再利用はしない。時刻・IDを消すだけでは乱数、履歴、シーン依存を混同する。中間計算のcontent identityと表示要求のdelivery identityを分け、状態が同一と証明できる供給元だけで共有する。

`FrameCacheStore.Put` はサイズと予算を主に検査し、軽いrenderを採用から除外しない。軽いfixtureでcache OFFよりRAM復元が遅かった [既存測定](GPU_FRAME_RETENTION_RESULTS_2026-10-01.md) と整合する。GPU hitの高速化だけでこの差は消えない。

## AE本体の公開仕様との照合

| 公開仕様 | 現行の差／判断 |
| --- | --- |
| 軽いrenderはimage cacheへ保存せず、取得よりrenderが安い場合はdisk保存しない [W1] | render／復元／保存費用と需要を観測した採用判断が必要。固定の効果名や根拠のないmsしきい値で判断しない |
| footage・layer段のcache、Undo、別時刻・複製構成の再利用 [W1] | Undoは対応済み。素材／layerの中間保存と意味的な同一性共有は未実装 |
| idleは8秒、3順序に加えてWork Area／拡張範囲／Entire Duration [W2] | 待ち時間と3順序は対応。現在は全タイムラインだけで、work area範囲の契約・設定は未実装 |
| Cache Before Playback、preview fps・skip・resolution・停止時挙動 [W3] | YMM4標準再生へ供給する方式。音声時計も含むbufferingと再生前キャッシュは未実装 |
| RAMに収まらない区間もdiskとRAMを循環してpreview [W3] | disk read-aheadはあるが、約0.5秒の窓とmiss時の通常描画。再生deadline／連続供給余裕に応じた適応は未実装 |
| 無損失の圧縮フレーム再生 [W4] | diskはraw画素。圧縮算法・file format・thresholdは公開仕様から確定できない |
| memory balancer、容量設定、RAM／diskの個別purge [W1] | 自動RAM配分は独自policy。Adobeアプリ間のbalancerは使えない。diskは固定4 GiB、purgeは一括中心 |

メモリ解説には「diskはリアルタイムpreviewに使わない」という旧記述が残る一方、2026-03-05更新のPreviewingにはdiskを使う長時間previewが明記される。圧縮も同ページのBeta表記と新機能ページの記述に差がある。ここでは現行機能の個別ページを優先し、26.3実機で挙動を照合する。

SDKからAE全体のscheduler、圧縮codec、全LRU／価値算式、GPU→RAM→diskの昇降格順序、音声bufferのwatermarkまでは分からない。これらは未確定として扱う。

## 次の実装に必要な契約

1. **依存を報告する任意の処理の入口**: 計算classとschema、入力状態token、入力の時間範囲、外部素材／隠れた状態、画素要求、thread／context制約を処理側が報告する。observerの型発見から安全宣言を作らない。
2. **計算registry**: 即時のcached-only lookupとcompute要求を分け、Missing／Computing／Ready／Failedを区別。同じ完全キーと互換contextで共有し、計算ownerとconsumer取消を分離する。UIは計算を同期開始しない。
3. **結果receipt**: 不変value、依存snapshot、region／format、所有資源の概算、解放処理、元の描画費用を保持する。退避しても借用中の値を壊さず、部分結果を完成frameとして使わない。
4. **価値判断**: 先読み開始前と採用直前に、保存済み／競合job／需要／予算／描画・復元費用を確認する。有効性は別に再確認し、採用しない結果でも要求consumerへは正しく供給する。
5. **schedulerと中間cache**: requestのpurposeと世代、音声時計、work area、供給deadlineを統合する。stage／時間範囲の依存を追えるところから中間保存を追加する。

これらは提案で、今回の調査で実装した機能ではない。最初は描画費用と復元費用の記録・比較、依存を報告できる処理の契約から進める。値の有効性を緩めたり、描画検証を省く最適化はしない。

## 参照

SDKヘッダーは全文転載せず、固定版の契約名と比較結果だけを記載する。

[S1]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_ComputeCacheSuite.h
[S2]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_EffectSuites.h
[S3]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_Effect.h
[S4]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_GeneralPlug.h
[S5]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_EffectGPUSuites.h
[S6]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_HashSuite.h
[S7]: https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_CacheOnLoadSuite.h
[W1]: https://helpx.adobe.com/after-effects/desktop/memory-storage-performance/memory-and-storage/memory-storage1.html
[W2]: https://helpx.adobe.com/after-effects/desktop/render-and-export/multi-frame-rendering/multi-frame-rendering.html
[W3]: https://helpx.adobe.com/after-effects/desktop/view-and-preview/preview-video-and-audio/previewing.html
[W4]: https://helpx.adobe.com/after-effects/desktop/what-s-new/whats-new.html
