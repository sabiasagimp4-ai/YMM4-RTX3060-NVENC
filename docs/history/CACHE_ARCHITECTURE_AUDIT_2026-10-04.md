# Design basis before implementation

Goal: reuse the correct rendered YMM4 frame across valid interactive operation sequences; an optional cache must not make the renderer less correct.

Requirements: interactive demand wins over speculation; reuse survives A/B/undo when content matches; edits change only dependent identities; preview geometry and export semantics stay explicit; persistence requires stable render provenance; budgets include transient and borrowed resources; failures revert to the host renderer.

Invariants: complete identity; certified dependency snapshot; ready pixels rather than decode placeholders; separate immutable content identity from request/publication epoch; atomic purge admission; cancellation must not poison other consumers; exact output interpretation; immutable borrows survive eviction; bounded pending jobs/bytes; owning-context GPU use; no external callbacks or blocking I/O under coordination locks; restart validates identity and integrity; selective invalidation includes temporal dependencies; unknown contracts bypass.

Architecture derived from requirements: host adapter certifies snapshots and render readiness -> frame request (exact time + output specification + dependency digest + renderer provenance) -> demand/speculation coordinator with publication lease -> bounded immutable RAM/disk store and context-owned GPU borrows -> advisory residency UI. Pixels have content identity; edit/session/purge tokens govern requests, never substitute for content identity. CPU storage and GPU rendering do not share an execution lock.

Competing designs to evaluate: (A) global revision-keyed cache (simple but loses selective/A-B reuse); (B) certified content-addressed completed frames with explicit publication transactions (preferred if host boundary is provable); (C) full effect DAG cache and MFR (requires dependency/thread-safety contracts absent from the public host integration); (D) trust only observed pixels (cannot prove future validity). Choose correctness, robustness, simplicity, then speed.

Unproven host facts remain audit findings, not permissions to invent APIs or enable MFR.

---

# 監査結果：2026-10-04

## 結論と範囲

不変な完成フレームを内容で識別してRAM・disk・context所有のGPU画像へ保存する方向は残す。しかし、現行構造だけではAEに近い安定したpreview基盤には足りない。host input snapshotを保証する境界と、再生・音声時計・foreground要求・背景仕事を調停するbrokerが必要。容量増加や全frameの並列化では解決できない。

対象mainは `6adde7c5e9415d5f06e65cb191ca6d7b1ca6f4b5`。mainは変更しない。作業ブランチは `codex/cache-architecture-audit-2026-10-04`。上の設計基準を `bb7c1f6` で実装前に記録し、コードと公式配布YMM4 4.56.1.0 DLLを比較した。依存DLLは配布manifestのhash・sizeと照合した。描画順、resource生成、group、playerの処理を確認した。ホストDLLと逆コンパイルしたコードはリポジトリへ追加しない。

これは実施した集中監査と改善の成果であり、1週間が経過したことや全有効状態空間を証明したことを意味しない。GUI実操作、RTX 3060実機、音声付き再生の測定は未実施。

## 1. 目的・要件

停止、seek、逆移動、編集、Undo、別projectへの移動という操作系列で、現在要求した画素を安定して供給する。同じ入力には同じ結果を返し、不明な入力では通常描画する。cache成功率を画素の正しさと交換しない。

現在表示する画像は、将来使う画像より優先する。consumer取消はそのconsumerの関心を失わせるが、他consumerの計算まで壊さない。内容identityと仕事の公開世代を分離し、A→B→AやUndoで内容が戻れば有効な結果を再利用する。previewのviewport／zoom／pan、exportの解釈、正確な時刻、FPS、decoderの実状態、font、rendererの実装と環境を入力として扱う。

## 2. 必要なcorrectness invariants

| ID | 必要な性質 | 判断・現在の境界 |
|---|---|---|
| I1 | hitは同じ要求の通常描画と同じ画素・解釈を返す | 最高優先度。速度より先に検証する |
| I2 | identityは全入力、入力の結び付け、正確な時刻、出力仕様、renderer provenanceを含む | 同じtokenでもどのslotに属するかが必要 |
| I3 | 内容等価ならobjectを作り直しても再利用する | hostがobjectを乱数seedにする処理だけ明示的session制限 |
| I4 | 内容identityと取消・公開世代を分離する | revisionを一律にキーへ混ぜてUndo再利用を失わない |
| I5 | 有効なcaptureだけをhit・表示・保存する | live出力の近道にも適用。callback再入後も検証する |
| I6 | 完成・decoder準備済み画像だけReadyにする | timeoutの透明画像や以前のsampleを成功として保存しない |
| I7 | 入力変更は依存する結果だけ失効させる | transitionの過去frame、nested、audioを含む。最小keyframe依存は未達 |
| I8 | seek方向やresource生成履歴でキーの意味が変わらない | 証明できない描画順はbypass |
| I9 | purge／store交換／取消前のproducerが新公開世代へ復活しない | 開始時のowner／世代を採用lockで照合 |
| I10 | tier間の違いはresidencyであり画素の意味ではない | RAM・disk・GPUで同じidentityと出力契約 |
| I11 | eviction・purge・登録解除が借用中結果を壊さない | 不変borrow、最後の所有が外れたときだけ破棄 |
| I12 | GPUは互換device/contextと同期完了を確認して採用する | 可変D2D graphをcontext間共有しない |
| I13 | consumer取消・失敗が他consumerや再試行を壊さない | 共通completionとconsumer待機edgeを別管理 |
| I14 | 待機循環や重複仕事が資源を無限に占有しない | 同一compute cacheの循環検出を追加。完成frame全体のsingle-flightは未実装 |
| I15 | coordinator lock内で外部callback・I/O・UI待機しない | 理想要件。tracker／GPU経路に未解決箇所あり |
| I16 | resident、in-flight、borrow、queue、一時資源を有界にする | 現予算はpayload中心。全working setの厳密予約は未達 |
| I17 | restartは要求identityと画素の完全性を確認する | 有効画素を別ファイル名へ置いたrecordも拒否 |
| I18 | OOM・disk full・cancel・render failureで安全に劣化し回復後に再試行する | hostへfallback。停止したnative I/O／device lostの実機注入は未検証 |
| I19 | 無効化確認だけのために余分なrenderを要求しない | witnessと依存証明。証明できない入力は通常描画 |
| I20 | residency UIを現在画素や再生準備完了の証明にしない | 帯は助言。UIがrenderを待たない |

## 3. 理想アーキテクチャと競合仮説

identityは概念的に `H(schema, renderer/environment, output specification, exact time, ordered dependency bindings)`。bindingにはclass／schema、意味上のslot、内容token、時間範囲、必要なcontextを含む。project／request世代は要求と公開許可を制御し、内容キーへ一律に追加しない。

| 責務 | 本来の境界 | 既存構造の判断 |
|---|---|---|
| Host adapter | hostが保証する同期境界で不変snapshotとreadiness・描画順を証明 | 反射／Harmony／MVID契約を隔離。JSON監視だけを原子snapshotと扱わない |
| Dependency index | 内容と時間依存から編集影響範囲を導出 | `FrameDependencyIndex`を残す。最小effect／keyframe依存は段階的に追加 |
| Request broker | demand優先、仕事共有、context能力、consumer取消、公開permit、再試行 | foreground／idleの完成frame全体には未実装 |
| Immutable storage | RAM／diskは不変record、GPUはcontext所有画像のborrow | LRU、bounded queue、disk worker、GPU参照所有を残す |
| Playback transport | frame準備・deadline・音声clock・停止／scrubを調停 | wall-clock駆動playerに保存機能を足すだけでは不足 |
| Residency UI | 現identityの保存状態を非同期に提示 | 既存帯を残す。再生可能性の保証にはしない |

仕事の状態はMissing→Reserved→Rendering→Validating→Ready。取消／失敗／失効はReadyを経由しない。consumerは不変結果を借用する。RAMにあるかdiskだけかというresidencyは別の状態。背景cloneはforegroundのUIや音声transportを所有しない。可変TimelineSourceを汎用compute cacheへそのまま登録して並列化する設計は採らない。

| 設計仮説 | Correctness／robustness | Simplicity／performance | 結論 |
|---|---|---|---|
| 全project revisionをキーにする | 未通知の隠れた入力は解決しない | 単純だが無関係frame・Undoの再利用を失う | request失効に使う。内容identityとしては不採用 |
| 証明付き内容addressed完成frame＋公開transaction | host snapshotの証明範囲で段階的に成立 | 既存storeを活用し、保守的bypassから拡張 | 今回の改善方針 |
| effect DAG／stage cache＋MFR | 最小依存・thread safety・checkout契約が必要 | 高い再利用余地と大きい複雑性 | host契約取得まで全面導入しない |
| 観測画素や型名から将来の安全性を推定 | hidden state・履歴・外部変化を証明できない | 見掛けのhit率は上がる | 不採用 |

AE比較は公開仕様とSDK APIの契約に限定する。RAM／diskの公開説明、Compute Cacheのkey・receipt・共有計算、MFRのcallbackとlockの制約を比較材料とした。AEの内部scheduler、record形式、価値算式は推測しない。[Adobe memory/storage](https://helpx.adobe.com/after-effects/using/memory-storage1.html)、[Compute Cache API](https://ae-plugins.docsforadobe.dev/effect-details/compute-cache-api/)、[MFR guide](https://ae-plugins.docsforadobe.dev/effect-details/multi-frame-rendering-in-ae/)、既存[SDK比較](../AE_CACHE_CONTRACTS.md)を参照。

## 4. mainとの差・根本原因・実変更

| mainの問題 | 原因と反例 | 実施した変更 |
|---|---|---|
| disk checksumと要求キーが結合していない | 有効A recordをB名へコピーすると同じ長さの別画素をB hitとして返した | 要求キー32 byte＋展開画素のSHA-256。raw／圧縮02 schema。旧01を破棄 |
| producer開始時のstore公開世代がない | 古いproducerがpurge後に採用を始め、新世代へ入り得る | `Publication(Owner, Generation)`を仕事前に取得しClearと同じlockで採用。preview／export／idle primeへ接続 |
| provider tokenをsortする | 同じモデルのslot A/Bで赤／青のhidden stateを交換してもmultisetが同じ | 探索slot順を保存して結合。等価な新snapshotはreuse |
| live出力の近道がcapture validationを省く | capture後・lookup前の背景色変更で古い出力をhitした | liveにも`StillCurrent`。実host negative controlで旧guardのwrong hitを検出 |
| revision中心の環境検証 | code trust、font、reader、描画設定変更途中のdescription／captureを採用し得る | environment witnessを保存・採用・引渡しで照合。provider再入後にも再検証 |
| owner threadでの自己再帰検出だけ | 独立worker A→B／B→Aが取消timeoutまで停滞 | ExecutionContextでownerを伝え、待機辺追加前に循環検出。consumer数を数えfinallyで解除 |
| item hash集合がhost描画順の履歴依存を証明しない | dictionaryのtop／Z／Layer sortで同値順は挿入順に依存。prefetch・並行生成でmodel順と一致する保証がない | 同じLayer／topの重なりを保守的bypass。transition／nested／wideへ伝播 |

本体変更は `b20c97f`。headerは48 byte、directoryは`frames-v1`のまま。digestの意味を変えたためmagicを02へ変えた。旧01を安全な結果として移行しない。これは暗号化や悪意ある攻撃に対する認証を追加したとの主張ではない。

### 撤回した仮説と再評価

最初はitem配列順をhashへ足す仮説を検討した。公式hostを確認するとresource挿入順をmodelだけで保証できず撤回した。異なるLayerではsortが順を定めるので、従来のorder-insensitiveなreuseを維持し、曖昧区間だけをbypassする。実際にはZが異なる同一Layerまで対象外になるfalse missはwrong hitより先に受け入れた。

性能fixtureは120 shapeを100 frameの同一Layerへ置き、最初の20 frameに重なりがあった。新判定で20 frameがbypassされ、100枚保存の検証が失敗した。元fixtureの20拒否／80許可を明示的に確認し、速度測定は重なるshapeを別Layerへ配置した。保存数assertionと安全判定は緩めない。

## 5. ストレスケース・検証

| 層 | 反例・oracle | 確認内容と限界 |
|---|---|---|
| Disk | 有効raw／compressed A recordのBへの置換、restart | 旧実装wrong hitを再現。修正後は拒否、正常Aはreuse |
| Publication | producerを止めpurge後に完了、RAM-only／disk、owner交換、dispose、borrow | 旧permit拒否、新permit採用、purgeでborrow不変 |
| Dependency | 同モデルslot交換、等価snapshot object作り直し | 旧キー衝突を再現。修正後は区別し等価状態は一致 |
| Concurrency | 独立root cycle、async descendant cycle、正常diamond、同一edge片方取消 | timeoutに頼らず例外・再試行。正常DAGはleaf1計算、3結果を各1回破棄 |
| Interval | seed846371の500 layout、65,500 frame状態を独立pairwise oracleと比較 | overlap境界、top差、transition前frame、nested伝播 |
| Real host keys | 変更／復元／Undo／Redo、同metadata素材交換、missing、provider再入、環境witness | 実4.56.1.0 DLL。GUI実操作ではない |
| WARP | alpha、負bounds、奇数サイズ、preview変換、disk restart、部分編集／Undo、readback、GPU borrow | actual TimelineSource画素一致と資源寿命 |
| Negative control | live validation guardだけ削除して同じhostテスト | `Edit between capture and live lookup reused stale output`で失敗、guard復元後は成功 |
| Host操作列 | seed31047、2project、4phase、296表示を別sourceの未cache画像と比較 | 前後／rapid seek、Paused／Playing、即時編集、A→B→A、project切替、size／FPS、RAM／GPU縮小、purge。GUI／音声clockは対象外 |
| 既存suite | store concurrent index、budget、破損、locked-file epoch、readiness、file lease、native queue／MP4、Python解析 | LinuxとWindowsで対応suite。NVENC hardware処理は未実施 |

Linuxの追加adversarial suiteとhost probe cross-buildは成功、警告／errorは0。[run 37136479029](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37136479029)でWindowsのreal host keys・WARP pixel checksは成功したが、run全体は上述の性能fixtureで失敗した。追加操作列とfixture修正を含む `2bb16dbaac5efbf40ac8f2a7bcc4cc01f4644d8f` の [run 37161848718](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37161848718) はportable／host／windowsすべて成功。Windows logは296表示の画素一致、GPU保持・active-borrow退避・遅延budget変更・viewport／edit／purge無効化・source破棄、元fixtureの20拒否と修正fixtureの100 frame適格性を記録している。native checksも成功。任意実行のrtx-check jobはskippedで、RTX実機／NVENCの確認ではない。既存テスト合格を全状態空間の証明と扱わない。

再実行入口は `tests/CacheAdversarialChecks`、`tests/StoreChecksHarness`、`tests/ReadinessChecks` の各csprojに対する `dotnet run -c Release`。Windowsでは `tests/CacheChecks`、`tests/HostCacheProbe` に `-p:YMM4DirPath=<verified-host-directory>` を指定し、引数にhost directory、後者には `--gpu`／`--preview-performance` を渡す。workflowにnegative controlとnative／file lease検証も含む。

## 6. 未解決の高優先度問題・まだ破壊できそうな系列

| 優先度 | 構造上の不足 | 次の実装単位と完了条件 |
|---|---|---|
| P0 | 可変sceneのbackground記述。PropertyChanging／Changedと前後revisionは原子snapshotの証明ではない。通知間に止まる編集や監視外入力 | host同期境界から不変input取得。editとcaptureを交差させ混合snapshotを公開できないことを証明。境界が得られない処理はbypass |
| P0 | tracker／GPU lock内のprovider callback・file確認・host処理。別thread編集完了を待つcallbackでlock循環し得る | candidate取得→lock外検証→lock内短い採用へ分離。外部lockを持つproviderの再入・待機を注入 |
| P0/P1 | 手動assembly trustはplugin純粋性を証明しない。履歴・hidden乱数・外部入力未報告で不足キー | 明示的dependency／determinism契約。未証明処理をbypassしtrust前提を拡大しない |
| P1 | playerはwall-clockでPositionを進め遅れたframeの表示を省く。storeだけではcache-before-playback・音声同期がない | 音声transportとreadinessを調停するbroker。停止／scrub／seek／play中miss政策、drift・UI応答・連続供給を測定 |
| P1 | foregroundとidle cloneの同じ完成frameをsingle-flightで共有しない | context能力を含むbroker。demand優先、consumer取消、store交換、purgeを独立化。UIで他renderをwaitしない |
| P1 | item全体hash・global character依存は最小時間区間より広い。無関係編集でも進行中revisionは失効 | per-stage／time-range証明へ縮小。区間外変更とA→B→Aで余分なrender数を比較 |
| P1 | 予算は全process working setではない。borrow、host graph、driver、pool、一時展開が追加 | 保存開始前の予約／admission、in-flight診断、OOM／pressureと回復注入。payloadを実VRAM全量と呼ばない |
| P1 | Clearはdisk barrier、Disposeはworker Joinを待つ。native I/O停止で有限時間の復帰保証なし | 非同期停止とdeadline、epoch永続化失敗の可視化。停止disk／full volume／shutdown中purgeを注入 |
| P1 | device lost、RTX queue圧力、実動画decoderと音声を伴う操作列は不足 | 実Windows GUI＋RTX 3060の再現script／trace。WARP／NoNvencをhardwareの代用にしない |
| P2 | font世代poll最大10秒、Z差があっても同一Layerはbypass、cache間／ExecutionContext抑制の循環は追跡外 | font同期契約、実描画順witness、cache間依存APIを明確化 |

今回も「本来の目的に最適な構造か」への答えは「不変な内容storeは適切。host snapshotとtransport／brokerは未完成」。これらを解決する前にAE同等、全操作安全、MFR安全、全VRAMの予算保証とは表現しない。

## 7. 作業ブランチと主要コミット

| Commit | 内容 |
|---|---|
| `bb7c1f6` | 実装前の目的・invariants・競合設計の基準 |
| `b20c97f` | identity、publication、provider binding、live validation、環境witness、wait cycle、draw-order eligibility |
| `a87fe56` | negative controlの想定process終了codeをCIが再度失敗扱いしない修正 |
| `2bb16db` | real host操作列、正常／循環compute DAG、曖昧fixtureと性能fixture |

mainへmergeせず、専用ブランチ上の監査・実装・検証をレビュー可能な変更として提出する。

## 8. レビュー後の修正と計測（2026-10-04）

レビューの指摘を直し、実 YMM4 Lite 4.56.1.0＋WARP で測った。計測だけを足した `3dfa26d`（[run 37165573054](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37165573054)）と、修正の `64c9c6a`（[run 37165987556](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37165987556)）は、どちらも portable／host／windows 成功。

### 8.1 修正

| 指摘 | 修正 | 確認 |
| --- | --- | --- |
| 停止中の先読みは、消去の世代と公開許可を描画の後に取っていた（§4 の「idle primeへ接続」は成立していなかった） | 描画の前に取る（`PrimeTicket`）。消去後に保存へ進んでも拒否される | HostCacheProbe：描画と保存の間の消去で保存されない。消去後に取った許可では保存される |
| 同じフレームの使い回しの判定で、capture の全検証（`StillCurrent`）を条件の先頭で毎回、`cacheGate` の中で実行 | 安い比較（キー・出力・世代）の後、最後に実行 | 下の同一 run の比較。CI の否定対照は新しい位置の検証を外して実行し、従来どおり失敗を確認 |
| 公開許可の取得が、描画ごとに保存庫のロックを取る | ロックを取らずに読む（採用時に Clear と同じロックの中で照合）。世代の更新を原子的にした | 既存の publication 検査 |
| 環境の確認（信頼したコード・フォント・読込プラグイン・描画設定）が 3 か所に別々に書かれ、tracker の確認には描画設定がなかった | 1 つの述語にまとめた。tracker の確認も描画設定を見る（通知なしの設定変更で、全 capture が検証で落ち続けることがない）。capture の検証で環境を 2 回確認するのは、callback（provider・パス確認）があるときだけ | 実ホストのキー検査 |
| 計算キャッシュの循環検出が、要求した時点の関係で判定し、待たない要求（待たない ComputeAsync、計算中に起動した処理）まで例外にした | 循環になる待ちは、待たずに値なしで答える（同期は `Computing`、非同期は `null`）。例外にしない | 待たない要求の検査：修正前は `Cyclic compute cache dependency` 例外、修正後は成功。本当の循環（独立 root、async の子孫）も停止せず完了 |
| ディスク記録の差し替え検査が、索引の準備を待たずに照会し得た（合計サイズは索引より先に加算される） | 差し替えていない記録が読めるまで待ってから確かめる | adversarial 検査 |
| `TryCreate` の対象外の理由が、同一レイヤーの重なりを含んでいなかった | 文言を更新 | — |

### 8.2 計測

CI のランナーは run ごとに速さが違う（キャッシュなしの描画：`3dfa26d` 1.31 ms／frame、`64c9c6a` 0.87 ms／frame）。run をまたぐ絶対値は比べず、同じ run の中の比較と、1 回あたりの小さな処理の時間を示す。

| 計測（実ホスト） | PR（`3dfa26d`） | 修正後（`64c9c6a`） |
| --- | --- | --- |
| 同じフレームの使い回し（200 回）p50／p95 | 0.034／0.044 ms。同じ run で検証を外すと 0.019／0.028 ms | 0.017／0.028 ms。同じ run で検証を外すと 0.018／0.026 ms |
| `KeyCapture.Validate`（provider なし） | 0.92 µs | 0.19 µs |
| `KeyCapture.Validate`（provider 1 つ） | 0.77 µs | 0.43 µs |
| `KeyDependencyTracker.TryCapture`（記述済み・ファイルなし） | 0.24 µs | 0.89 µs（描画設定の確認を足した分） |
| `FrameCacheKey.DrawingSettings`／`SourceReadersMatch` | 0.47／0.04 µs | 0.26／0.03 µs |

PR で足した検証は、使い回し 1 回あたり約 0.015 ms 増やしていた。修正後は、検証を外した場合と差がない。どちらも 1 フレームの描画（約 0.4 ms）より十分小さい。レビューで 0.46 ms と書いたのは、元の検査の 8 回だけの計測で、揺れだった（200 回では上の値）。

### 8.3 同一レイヤーの描画順の実測

重なる 2 つの文字アイテム（赤・青）で、同じフレームを 17 通りに描いて比べた（新しい source を 11 回、先に別のフレームを描く、前方・後方に連続で描く、アイテム一覧の順を入れ替える）。

| 配置 | 結果 |
| --- | --- |
| 同じレイヤー | 一覧の順を入れ替えたときだけ絵が変わった。シーク履歴や新しい source では変わらなかった |
| 別のレイヤー | 一覧の順を入れ替えても、すべて同じ |

同じレイヤーで重なると、YMM4 はアイテム一覧の順で重ねる。main のキーは、各アイテムの内容の集合（順序なし）なので、一覧の順だけが変わると、違う絵が同じキーになる。この PR の「同じレイヤーの重なりは通常描画」は、この誤りを防ぐ。ただし理由は §4 の「prefetch・並行生成・シーク履歴で順が変わる」ではなく、一覧の順で、その揺れはこの実験では再現しなかった。重なるアイテムだけ一覧での相対順をキーに含めれば、対象のまま正しくできる（未実装）。

`TransitionItem` と `VoiceItem` は `IVideoItem`（レイヤーを持つ）なので、この判定の対象になる。トランジションをクリップと同じレイヤーに重ねて置く使い方なら、その区間は通常描画になる（実プロジェクトでの確認が必要）。

### 8.4 §4 の表の訂正

- ディスク記録の差し替え：通常の動作では、記録のファイル名と中身は常に一致する。キャッシュのファイルを外から複製・改名した場合への保険（安価なので残す）。
- 公開世代：プレビューと動画出力では、main がすでに防いでいた（世代の確認と消去が同じロックの中）。抜けていたのは停止中の先読みで、§8.1 で直した。
- 取り込み後の編集と使い回し：1 回の描画の途中に編集が割り込んだ場合で、効果は直前の状態を 1 フレーム表示すること（保存はされない）。検証は残し、§8.1 のとおり最後に回した。
