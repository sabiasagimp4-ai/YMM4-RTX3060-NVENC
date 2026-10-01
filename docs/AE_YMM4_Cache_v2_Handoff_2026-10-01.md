# Claudeへの引き継ぎ：YMM4キャッシュ v2

作成日：2026-10-01（日本時間）  
対象：[sabiasagimp4-ai/YMM4-RTX3060-NVENC](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC)  
参考SDK：[sabiasagimp4-ai/aesdk](https://github.com/sabiasagimp4-ai/aesdk)

## 1. 依頼と到達目標

既存のClaudeブランチを読み、AE SDKから得られるキャッシュの契約を参考に、YMM4のプレビューキャッシュを段階的に改善してください。

当面の到達目標は、**通常プレビューで完成フレームが蓄積され、RAMに収まらない保存済みフレームも再レンダーせず供給でき、編集・Undo・素材更新・取消で誤った画像を表示しないこと**です。バーの色や単純な同一フレームの高速化だけでは完了としません。

最初に現在のHEAD、既存の `CLAUDE_HANDOFF.md`、適用される `AGENTS.md`、未コミット変更を確認してください。本書は過去の解析を引き継ぐ資料です。最新HEADで修正済みの問題を重複実装せず、既存作業を維持してください。

まずP0と計測を行い、その後、通常プレビューの保存とdisk deliveryを一つの利用経路として完成させてください。全面的なReceiptGraph化、MFR、各effect段の保存は後段です。

## 2. 調査対象と確度

| 対象 | 調査した版 |
|---|---|
| AE SDK | After Effects SDK 26.5 Windows版、`390a34d0cedba002814bd1879ec4c604bff46bc2` |
| YMM4主解析 | `claude/frame-render-readiness`、`5db10319319484f67661688ab8919ceede53d1ad` |
| 解析後に確認した版 | `8531425e0815aab895b8de9d9269a8efc213290a`。上記との差はGUI smoke scriptで、キャッシュ本体は同一 |

以下の「現実装」はこの解析対象を指し、現在HEADを保証しません。

- **確認済み**：SDKヘッダーのAPI契約、対象コードの制御経路。
- **要検証**：時刻キー衝突が実hostで誤画像を生むか、並行アクセスが実際に破綻するか、各処理の性能。
- **設計提案**：YMM側のクラス分離、優先順位、依存グラフ、保存の選別。

本調査ではWindows/YMM4/RTX3060実機で新しい性能測定を行っていません。AE内部の永続キャッシュ形式・圧縮codec・厳密なRAM↔disk移行・eviction式はSDKから確定していません。

## 3. 維持する既存の土台

次を捨てず、変更時の回帰試験に含めてください。

- 内容由来のキーと、in-flight結果を検証するrevision/generationの分離。
- active root itemsを分けた `FrameDependencyIndex` と、transition等の時間依存。
- `KeyCapture` 相当の開始時の捕捉・採用時の検証、取消、cache epoch。
- 素材のfingerprint、identity、file leaseによる変更対策。
- デコーダーの完成判定。未完成画像を保存しないこと。
- checksum、容量制限、purge、保存失敗時の通常描画への復帰。
- host版ごとの契約確認と、映像・選択枠の画素/挙動の検証。
- viewportを含む現在の正確なキー。安易にzoom/pan依存を外さないこと。

`revision++` は保存済みフレームを必ず全削除する処理ではありません。無関係なitemの変更後も内容キーが同じなら旧entryを使えます。**revisionそのものを内容キーへ追加してUndo再利用を失わせないでください。**

## 4. AE SDKから採用する契約

| 根拠 | 確認できる契約 | YMMへの適用 |
|---|---|---|
| `PF_GetCurrentState` / `PF_AreStatesIdentical` | 入力状態の比較と時間範囲 | 必要な入力状態を捕捉し比較する |
| `GuidMixInPtr` | 通常parameter以外の描画依存を追加 | 素材・reader・seed・外部状態を漏らさない |
| `AUTOMATIC_WIDE_TIME_INPUT` | 宣言された時間方向の入力参照を追跡 | 時間依存の伝搬を明示する |
| Compute Cache | 同一キーの計算共有、結果の借用/返却、size申告 | SingleFlight、所有権、容量計数 |
| RenderSuite5 | 投機計算の開始前と完了後にworthwhileを確認し、変更も検証 | Idleの採用判定を二段階にする |
| `CheckinRenderedFrame` | 結果と概算描画時間を渡す。時間単位は60Hz | 計算費用を記録する。独自の選別は実測する |
| `IsRenderedFrameSufficient` | 保存済み描画条件が新要求を満たすか判定 | 将来の要求充足判定を研究する |
| CanvasSuiteの部分receipt | effect数を指定した結果の有効性をArtisanが確認できる | 将来の部分結果再利用を研究する |

### 読み違えてはいけない点

1. 入力状態、frame receipt、Compute Cacheの借用receiptは異なる概念です。すべてを一つのReceiptクラスにまとめないでください。
2. Compute Cacheの `wait=false` は、未計算なら呼び出した側で計算します。他threadが計算中の場合に待たずに返す指定であり、非同期開始APIではありません。
3. Compute Cacheはプロジェクトとともに永続保存されるdisk cacheではありません。
4. `GlobalReceipt + ItemReceipt + FileReceipt` は参考にしたYMM側の提案です。AE内部がこの構造であるという証明はありません。
5. 部分receiptはArtisan側の再利用を支える契約であり、AEが各effect段を常にdiskへ保存する証明ではありません。
6. 描画時間を申告するAPIがあっても、AEのeviction式やschedulerの内部実装は分かりません。
7. `AE_CacheOnLoadSuite.h` はpluginの起動時loadに関するAPIで、persistent frame disk cacheではありません。

## 5. 対象コードで確認した課題

| 箇所 | 確認した挙動 | 改善の目的 |
|---|---|---|
| `FrameCacheStore.TryGet` | RAM missはreadを予約しfalse。diskにあっても呼び出し元が通常renderへ進む | diskから実際に供給する |
| `TimelineFrameCache.Postfix` | 通常Playing/Pausedの完成画素を複数frame用storeへ保存する経路が限定的。主な保存はidleとexport | 普段の操作でも保存を増やす |
| `KeyDependencyTracker` | coldのbackground fingerprintは `cachedPaths` 全体を対象にする | 要求frameの素材だけで準備できるようにする |
| `FrameCacheKey.TryDescribe` | TachieItem、外部shape/resource等が広いbypassを起こす | 非依存frameまで除外しない |
| model生成 | whole-project JSON→JObject→item分解。編集後に再構築 | 所有item単位の更新へ段階的に移行 |
| `IdleFramePreRenderer` | 済み/計算中のkeyをskip/joinする統合が不足 | 重複計算と不要なcommitを防ぐ |
| preview key | viewport寸法、transform、DPI、format等を含む | 正しさを維持し、将来の分離を別途検証 |

全体JSON再構築が最大のボトルネックとは未測定です。disk供給不足や通常保存不足と分けて計測してください。

## 6. 実装順序と完了条件

### P0：キーの正しさと索引の並行アクセス

**時刻キー**：対象の `FrameTimeKey.For` はframe境界付近を最大1msの許容範囲で同じキーにします。30fpsのticks `9,999,999`、`10,000,000`、`10,000,001` はすべて `f30@30` になります。decoder sample境界や時間依存effectの入力は異なる可能性があります。キーの同一化は確認済みですが、実hostでの誤画像は未再現です。

まず正確な要求ticksと必要なtime step/FPSを含める方針を評価してください。正規化を維持するなら、hostへ渡す要求時刻も含めて同じ入力を生成する根拠が必要です。「frame番号が同じ」という理由だけでキーを丸めないでください。変更時はkey schemaを更新し、旧entryを誤利用させないでください。

**索引**：`GetResidency` は `_gate` 下で `_disk` Dictionaryを読みますが、対象のwrite/load/remove経路は同じlockを使いません。worker所有＋immutable snapshot、または全参照/更新に共通する短いlockで統一してください。file I/O中にUI/renderが必要とするlockを保持しないでください。

完了条件：sample境界前後のキャッシュON/OFF画素比較、索引read/write/evict/purgeの並行stress、取消とepochの回帰試験が通ること。

### P1：保存から表示までの経路を完成させる

1. 通常プレビューの完成画像を、安定した取得位置から所有snapshotとして保存する。
2. disk readをrenderとは別の仕事として扱い、playback方向・seek先に先行readを行う。
3. 表示要求をidle renderやdisk writeより優先し、queueとメモリに上限を設ける。
4. 読み込み完了時に要求・依存・epoch・viewportを再検証してから表示する。
5. readback、checksum検証、GPU upload、選択枠復元を含めて試験する。

disk-only hitをUIスレッド上の同期I/Oへ単純に置き換えないでください。先読み未完了時の扱いはhostの非同期表示能力を確認して決めます。前画像を使うなら暫定表示であることを内部で区別し、別時刻の画像を要求frameの完成結果として扱わないでください。非同期表示のhookがない場合は、その制約を明示してread-aheadから進めてください。

完了条件：idle OFFでも通常再生で保存が増えること。RAMから追い出した対象区間の2周目と再起動後の再生で、disk deliveryが成立し、対応frameのhost render回数が減ること。バー表示だけで合格にしないでください。

### P2：必要な素材だけを準備し、bypass範囲を限定する

`FileFingerprintService` 相当は素材ごとのReady/Pending/Failedを持ち、frameが必要とする素材集合だけを要求する設計にしてください。無関係な素材のfingerprint待ちで対象frame全体を止めないこと。Idleは近い将来に必要な素材を低優先度でprewarmします。

fingerprintの共有・必要時取得は、素材更新の検出やleaseによる保護を省く理由にはなりません。pathだけではなく、内容・identity・更新・reader設定を含む既存の正しさを維持してください。

cache eligibilityはitemの表示区間だけでなく、transition、nested scene、時間依存effect等の依存が伝わる範囲へ反映します。状態を識別できないitemがあっても、そのitemに依存しないframeは使えるようにします。未知のglobal依存は保守的なbypassを残してください。

外部effectを一括許可せず、必要なら固定版のadapterから始めます。パラメーター、素材、seed、時間範囲、隠れた状態、完成条件、算法版を説明できるものだけを対象にしてください。

完了条件：多数素材のうち対象frameに必要な素材だけで準備が完了すること。cache不可itemの非依存区間で保存/再利用が成立し、依存区間では安全にbypassすること。

### P2：SingleFlightとIdleの二段階採用

共有対象は**現在の完全な出力キーが一致する計算**です。時刻、usage、viewport、format、環境等が異なる要求を意味的なhashだけで合流させないでください。

| 呼び出し元 | 同じkeyが計算中のときの推奨方針 |
|---|---|
| Idle | skipし、新しい重複renderを開始しない |
| Live preview | UIをblockせず既存jobを優先。完了通知による表示を検討 |
| Export | 有効性と取消を確認しながら、互換のある結果を非同期で待つ |

LiveがPending後すぐ独立renderへ進む設計では重複は残ります。host制約でfallbackが必要なら、重複抑制の対象と限界を明示してください。単にNoWaitという名前を付けるだけでは完了しません。

consumerの取消と共有jobの取消を分けてください。Idle consumerが不要になっても、Live/Exportが必要なjobを巻き添えにしないこと。描画contextや借用GPU資源を複数threadから共有しないこと。同一keyという理由だけで既存のhost thread制約を越えないでください。

Idleの処理は次の順にします。

1. 必要な入力のsnapshotとepochを捕捉する。
2. 保存済みか、計算中か、先読みする価値があるかを確認する。
3. 同一keyの実行権を競合なく取得し、完成条件を満たすまでrenderする。
4. 依存・取消・epochを再検証する。
5. 既に結果が存在しないか、保存予算と将来需要を踏まえて採用価値を再確認する。
6. 検証からcommitまでの競合を既存guard等で防ぎ、結果を公開・保存する。

既存のcapture.Validate、generation保護を再利用し、別系統の検証を重ねて不整合を作らないでください。無関係な編集のために内容キーを変更する必要はありません。当初の取消判定が広くなる場合は、正しさを守る保守的制約として記録してください。

「保存する価値がない」と「要求された画像として無効」を分けてください。Exportが必要とする正しい結果を、disk容量不足やidleの価値判定だけで捨てないこと。

完了条件：互換条件の同一keyについてIdle同士・IdleとLive・複数consumerの重複計算を抑制できること。途中編集、consumer取消、purge、例外で待機が残らないこと。安全に共有できない経路は明示すること。

## 7. クラス設計のたたき台

これは責務の候補であり、全クラスを新設する指示ではありません。既存クラスに自然に収まるものは拡張してください。

| 責務名の候補 | 保持するもの・役割 |
|---|---|
| `DependencySnapshot` | immutableな入力状態と依存集合。採用時の再検証に使う |
| `RenderKey` | 内容、正確な時間、usage、描画条件、環境、schema |
| `CachedFrame` | 所有画像と寸法、region、format/alpha、描画費用等 |
| `FrameLease` | 借用期間。pool/GPU資源を使う場合の寿命とpinning |
| `RenderJob` | 実行中状態、consumer、優先度、取消、完了結果 |
| `FrameDelivery` | RAM/diskからの取得、read-ahead、upload、要求との照合 |
| `FileFingerprintService` | 素材ごとの準備・共有・更新検証 |
| `Scheduler` | 表示、読込、idle計算、保存の調停とbackpressure |

`render_ms` はrender自体の時間として計測し、key生成・readback・disk read/write・upload・表示待ちは別項目で記録してください。保存済み結果の再利用時に元の計算費用を取得時間で上書きしないこと。計測費用をevictionに使う最適化は実測後です。

## 8. 依存グラフとscene rasterは後段で進める

全体JSONからの移行は、まずitem所有者の特定と変更通知を整えて、変更したitemの状態だけを更新するところから進めます。Opacity等の単純propertyに加えて、animation、effect list、nested parameter、character、reader設定、global設定の変更を取りこぼさないことが条件です。

移行中は旧方式との照合を利用し、差異の理由を調べてください。incremental方式にしてから変更通知漏れを発見する順序を避けます。watcherの通知だけを外部素材の内容が不変である証拠にしないでください。

canonical scene rasterは、`TimelineSource` の合成後と `TimelineVideoPlayer` のviewport変換前に、再利用可能な画像を安全に取得できるかの調査から始めます。既存のzoom/pan画素一致問題を踏まえ、補間、pixel位相、clip、透明度、text AA、色/formatを試験してください。取得位置があることと、要求条件を満たすことは別問題です。

item/effect cache、stateful/feedback effect、MFRはさらに後段です。履歴依存には開始状態・checkpoint・過去編集からの伝搬が必要です。TimelineSourceを複数Taskから同時UpdateするだけのMFRは実装しないでください。

## 9. 検証と計測

| ケース | 必須の確認 |
|---|---|
| cache OFF/ON | 同じ要求条件の画素一致。単純な軽いprojectの悪化も測る |
| cold→2周目 | 通常保存が増え、host renderが減る |
| RAM超過/再起動 | diskから供給される。RAM hitと分けて数える |
| 時刻境界 | 境界±1tick、実動画、VFR等で異なるsampleを誤共有しない |
| 編集→Undo | 必要なframeだけキーが変わり、戻した状態を再利用できる |
| 素材変更/置換 | 対象結果を誤利用しない。無関係な素材待ちに引きずられない |
| Tachie/外部要素 | 非依存frameは使え、伝搬先は安全に扱う |
| 同一key競合 | 実render回数、Pending、join、取消の結果を確認する |
| seek/zoom/pan/resize | 完了した古い要求が新しい要求を上書きしない |
| 選択枠 | cached画像表示後の編集UIが正しく機能する |
| purge/破損/disk full/device loss | 停止や誤表示を起こさず、必要なら通常描画へ戻る |

live reuse、RAM hit、disk delivery、render miss、bypassを分けて集計してください。UI操作→正しいframe表示のp50/p95、frame drop、host render回数、経路別時間、queue量、RAM/GPU/disk使用量を記録します。

実プロジェクトと、RAM容量を超える区間を含めてください。単純Shapeの同じ時刻に対する直前CommandList再利用の倍率を、disk cacheやRTX3060全体の速度倍率として報告しないでください。NVENC throughputもpreviewとは別評価です。

実行できない試験は未実行と記載し、コードレビューやCI成功で実機性能を代用しないでください。新しい変更のHEADと試験対象commitを対応させてください。

## 10. Claudeに求める成果物

1. 最新HEADで、本書の各課題が残っているかを短く整理する。
2. 既存クラスを活かした変更案と、P0/P1の実装を小さい単位で進める。
3. 各変更で意味のある正しさ試験と経路別計測を行う。
4. 引き継ぎ書を更新し、変更箇所、実行した試験、未実行条件、次の課題を残す。
5. 性能は実測の条件・経路・対象commitとともに報告する。

成果判定は「AE風のクラス名を追加したか」ではなく、**保存済みの正しい画像を要求時に供給でき、不要な計算を減らせたか**で行ってください。

## 11. 参照先

- [YMM4解析対象ツリー](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/tree/8531425e0815aab895b8de9d9269a8efc213290a)
- [FrameCacheStore.cs](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/blob/8531425e0815aab895b8de9d9269a8efc213290a/NVEncVideoWriterPlugin/FrameCacheStore.cs)
- [TimelineFrameCache.cs](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/blob/8531425e0815aab895b8de9d9269a8efc213290a/NVEncVideoWriterPlugin/TimelineFrameCache.cs)
- [FrameTimeKey.cs](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/blob/8531425e0815aab895b8de9d9269a8efc213290a/NVEncVideoWriterPlugin/FrameTimeKey.cs)
- [AE_ComputeCacheSuite.h](https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_ComputeCacheSuite.h)
- [AE_GeneralPlug.h](https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_GeneralPlug.h)
- [AE_Effect.h](https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_Effect.h)
- [AE_EffectSuites.h](https://github.com/sabiasagimp4-ai/aesdk/blob/390a34d0cedba002814bd1879ec4c604bff46bc2/AfterEffectsSDK_26.5_win/Examples/Headers/AE_EffectSuites.h)
- [Compute Cache SDK Guide](https://ae-plugins.docsforadobe.dev/effect-details/compute-cache-api/)
- [Artisan data types / receipt](https://ae-plugins.docsforadobe.dev/artisans/artisan-data-types/)
- [D3D11 multi-threadingの制約](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-intro)

SDK 26.5とWeb guideで古いsuiteのsignatureが異なる場合は、対象SDKのヘッダーを優先してください。本書と既存コードが異なる場合は、最新コードで確認し、その差を記録してください。
