# Claudeレビューへの引継ぎ：描画キャッシュ高速化

指定の依頼文・計画に従い、main `8aa03b2` に対応したレビュー修正PR #5の上に、1 → 4 → 2a → 6 → 8 → 5 → 2b → 2cのdraft PRを積み重ねました。全PRの宛先はmainです。merge、mainへのpush、release、tag、force push、他者のブランチへのpushは行っていません。3・7・9は対象外です。

全計測はWindows CI・WARPです。RTX3060や実GUIの音声／Presentを含む速度ではありません。任意RTX jobは未選択のためskipであり、全通常job成功とは区別します。前後の数値は、下記の計測runの同じWindows jobで各2回得たものです。少数の観測から安定した実機性能を保証しません。詳しい条件と失敗の記録は [SPEEDUP_RESULTS_2026-10.md](SPEEDUP_RESULTS_2026-10.md)、実装仕様は [CACHE_BEHAVIOR.md](../CACHE_BEHAVIOR.md)、ホスト監査は [HOST_CONTRACTS.md](../HOST_CONTRACTS.md) にあります。

## 2026-10-05 のレビュー対応

[Claude レビュー対応（2026-10-05）](SPEEDUP_REVIEW_RESPONSE_2026-10-05.md) に、独立 PR #14、固定 commit の任意計測、表示区間外の先読み、309 PNG / 109 MiB PSD の計測と改善、VRAM 1/2 上限と旧既定の一度だけの移行、今後の課題を記録しました。最終 head の通常 CI と追加の実測は各 PR に記録します。以下の古い CI・表は当初の実装を比較した履歴です。

## 項目1: ボイスの埋め込みデータをハッシュにする

- 状態: 完了
- ブランチ・PR: `codex/speedup-1-voice-cache-hash`、[PR #6](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/6)
- 変更: ホストのJSON設定を保ち、4 KiB以上の配列をSHA-256へ置換。記述内の重複ハッシュだけを省略。先読み複製は内容と位置を検証して復元し、監査済みボイスだけreadonly配列を共有。JSON外の音声パスも複製し、通知のない変更を採用前に拒否します。
- 検査: 4095／4096 byte、200モデル、200voice・18000frame、1 byte変更・復元、非通知WAVパス変更、字幕10frameの先読みhitと画素一致。[最終CI37190367478](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37190367478) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: [同一job37189797457](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37189797457)

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| 50voiceの記述文字数 | 8,967,625／8,967,626 | 273,225／273,226 | 大幅減、モデル上限内 | 約97%減 |
| 200／500voiceの文字数 | 35,844,513×2／89,598,513×2、対象外 | 1,066,913×2／2,654,513×2、対象 | 上限内でキー化 | 達成 |
| 50voiceの記述時間 | 232.33／142.39 ms | 110.62／90.14 ms | 小さくする | 両回短縮 |
| 50voiceの編集後回復 | 401.57／439.95 ms | 345.95／305.14 ms | 待ち時間短縮 | 両回短縮、250ms待ちを含む |

- 計画からの変更点: 可変公開配列の不変性を保証できないため、プロセス全体でhashを固定記憶しません。
- 残った危険・未確認: 200／500voiceの前は上限で処理が終わるため、後の全文処理と時間を直接比較しません。通知なし配列変更の常時監視、プロジェクト全体の原子的snapshotは未保証です。
- 利用者のPCで確かめてほしいこと: ボイスの多い場面を開き、ツールの「対象外」でモデル上限が減るか、先読み後の字幕が通常描画と一致するか確認。「再利用（同じ画像／GPU／RAM／ディスク）」と「新規描画」を比較します。

## 項目4: 素材の確認を変更通知にする

- 状態: 中止（通知への置換を中止し、同期leaseを維持）
- ブランチ・PR: `codex/speedup-4-file-generations`、[PR #7](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/7)
- 変更: watcher通知と30秒周期確認の前に、実ホストの新規sourceの画素が変わる反例を追加。通知の世代だけでは古い画像を採用し得ると確認。製品の同期lease／HostContentは維持しました。
- 検査: 通知遅延、実画素の変更、同期lease拒否、再起動までのbypass。既存の上書き・改名・置換・junction検査も維持。[最終CI37192134181](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37192134181) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: 上記最終CIの同一job

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| 24素材lease | 0.455／0.451 ms | 0.471／0.468 ms | 0 open/lookupと画素一致 | 未達、24 openを維持 |
| 500素材lease | 4.904／4.864 ms | 5.099／5.014 ms | 安全に再利用 | 256 open上限で対象外 |

- 計画からの変更点: 30秒再確認でも検出前の間隔を除けません。常時全素材の書込禁止も編集を妨げるため採用せず、計画の前提不成立として中止。
- 残った危険・未確認: 同期openの費用と同時保護256素材の上限は残ります。
- 利用者のPCで確かめてほしいこと: 素材上書き後の「対象外」に、YMM4再起動まで通常描画する理由が出るか確認。古い素材表示の更新はホストを再起動して確かめます。

## 項目2a: シンプル立ち絵

- 状態: 一部（監査済み同梱版・検証済み経路に限定）
- ブランチ・PR: `codex/speedup-2a-simple-tachie`、[PR #8](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/8)
- 変更: ホストの表情pickerを使い、選ばれる素材を区間ごとの依存にしました。未使用の表情とUI用フォルダーを依存から外し、表情効果の素材は保護。先読み複製のTachieFaceItemのCharacterも結び直しました。
- 検査: 通常画像／無発話非表示／GIFの各19境界frameでidle保存・全件hit・全画素一致。選択素材上書き拒否、未使用区間保持、模擬decoder未完成と復帰。[最終CI37202064034](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37202064034) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: [同一job37201316323](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37201316323)、2体・20voice・60秒・15fps・321×181、900frame

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| 2回目hit | 0／900、0／900 | 900／900、900／900 | 再利用率を増やす | 100% |
| 2回目の時間 | 2.327／1.421 ms/frame | 0.672／0.504 ms/frame | 短縮 | 両回短縮 |
| 2回目／自身のoff | 0.820／0.857 | 0.286／0.210 | 正規化して短縮 | 後は自身のoffより約71%／79%短い |

- 計画からの変更点: 新Harmony hookは追加せず、既存完成判定を使用。group、番号付き画像、同一layer表情競合、未知型を保守的に対象外にしました。
- 残った危険・未確認: 900frame計測には900画素比較を付けておらず、画素検査は別の各19frameです。GIF以外の実decoderタイムアウト、GUI fpsは未確認。
- 利用者のPCで確かめてほしいこと: 発話・上位表情・GIF切替を2回再生。「再利用」「RAM」「GPU」と、trace `timeline-update` のOutcome `live/gpu/ram` を確認し、選択素材上書き後の通常描画を確かめます。

## 項目6: GPUに置くフレームを増やす

- 状態: 完了
- ブランチ・PR: `codex/speedup-6-gpu-retention`、[PR #9](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/9)
- 変更: 読み戻しと検証済みの新規コピーをGPU保持へ移管。表示とcacheの所有権を分け、再生中は予算と空きに応じてRAMから最大1枚先回り転送。Auto上限をOS予算と余裕で制御し、保存済み手動値を維持。device lostでは保持と不正contextのpoolを捨てます。
- 検査: GPU再訪と8frame全画素、先回り、消去・予算縮小、模擬device lost・新source復帰、最後の資源0、旧設定と合成VRAM予算。[最終CI37207648136](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37207648136) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: [同一job37203584324](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37203584324)、1080p30fps・図形文字8frame

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| 最初の再訪GPU hit | 0／8、0／8 | 8／8、8／8 | GPU再利用を増やす | 100% |
| 再訪時間 | 4.314／1.839 ms/frame | 0.664／0.559 ms/frame | 短縮 | 両回短縮 |
| 再訪／自身のoff | 2.273／1.018 | 0.367／0.351 | 正規化して短縮 | 後は自身のoffより約63%／65%短い |
| 新規保存 | 13.805／12.750 ms/frame | 14.283／12.963 ms/frame | 初回短縮は主張しない | 改善未確認 |
| 保持bytes | 0／0 | 66,355,200／66,355,200 | 予算内 | 8枚を保持 |

- 計画からの変更点: 先回りは最大1枚とし、停止中は行いません。旧1/4上限を期待する検査の修正理由は先にPRへ記載しました。
- 残った危険・未確認: 実機のOS予算・他アプリ競合・実device lost・GUI fpsは未確認。CIの停止は課金／利用枠の開始前失敗で、利用者の復旧連絡後に最終headの成功を確認しました。
- 利用者のPCで確かめてほしいこと: Autoで同一区間を往復し「GPU」再利用と保持／予算を確認。trace `gpu-read-ahead`、他アプリ負荷時の縮小、画素一致、保存済み手動上限の維持を確認します。

## 項目8: 複数フレームの停止中描画

- 状態: 完了
- ブランチ・PR: `codex/speedup-8-parallel-idle`、[PR #10](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/10)
- 変更: 専用STA／device／複製／trackerを持つ最大4作業者へ通常frameを分配。Session描画はworker0だけ。連続した完了位置を記録し、取消後は全員joinしてからjobを解放。AutoはCPU／GPU余裕・観測した割当増分で制限し、WARPは1本です。
- 検査: 2本／4本／4本混在Session、1152frame全画素・全件hit・一度保存・所有threadでdispose。Barrier取消・消去・RAM0・join、Sessionの保存前purge、精度を保つモデル比較。[最終CI37210664561](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37210664561) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: [同一job37209699332](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37209699332)、321×181・60frame・200shape＋20text、前1本／後手動2本

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| idle fps | 21.1687／23.6159 | 27.0327／27.6434 | 速度向上・全画素一致 | 両回向上 |
| 60frame時間 | 2834.37／2540.66 ms | 2219.53／2170.50 ms | 短縮 | 両回短縮 |
| idle／自身のoff | 1.116663／1.010481 | 0.893534／0.884781 | 正規化して短縮 | 約20%／12%短縮 |
| 保存後hit | 60／60、60／60 | 60／60、60／60 | 全件採用 | 達成 |

- 計画からの変更点: AutoはGPU増分を測れるまで1本。WARP／未知も1本。非表示Session項目のidentityだけを比較用に揃え、数・他のJSON bytes・フレームキー・採用前検証を維持しました。
- 残った危険・未確認: private bytes増分は1本12,677,120、2本合計345,915,392で、GC・font等も含みます。WARPのGPU使用量は0。device別実VRAM、4本の実機効果、長時間GUI応答は未確認。
- 利用者のPCで確かめてほしいこと: 同じ重い場面の停止中描画を1→2本で比較。trace `idle-frame` のworker番号、Outcome、Detailのworkers／measured-worker-reserveを確認。再生・drag・編集で取消され、古い画面が保存されないか確認します。

## 項目5: 編集後の差分記述

- 状態: 中止（試作は検証済み、製品の全文記述を維持）
- ブランチ・PR: `codex/speedup-5-incremental-descriptions`、[PR #11](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/11)
- 変更: 通知・値・子の参照でJSON片を再利用する試作を検証。細かい数値・ベジェ・外部値も確認。目標未達で製品導入を中止し、試作をtest専用へ移動。製品serializerとの差をhook以外0にする継続検査を追加しました。
- 検査: 1000回の編集で全文JSONと依存パスが完全一致、非通知byte[]／外部値、Undo、Bezier、effect順等。最終製品では試作無効。[最終CI37240499744](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37240499744) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: [同一job37214642999](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37214642999)、1000item中1件編集

| 対象 | 前（2回） | 後：test試作（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| 図形の編集→capture | 570.6102／359.9301 ms | 420.2119／340.8551 ms | 10ms台 | 未達 |
| 文字の編集→capture | 150.1007／155.2291 ms | 145.4640／111.1527 ms | 10ms台 | 未達 |
| 再利用／直列化片 | 全文 | 999／1を各2回、全内容一致 | 内容一致 | 達成、速度目標は未達 |

- 計画からの変更点: 非通知変更を守る値検査を追加。依存列挙・JSON分割・索引は全文処理を維持したため目標に届きません。図形の正規化値は1回悪化。初回購読・メモリを増やして製品化する利益が未確認のため中止。
- 残った危険・未確認: この時間は250ms待ちや描画を含まないCPU記述です。既存の全文記述の費用は残ります。
- 利用者のPCで確かめてほしいこと: HostCacheProbeの `--edit-description-check`／`--edit-description-measure`／`--edit-description-measure-incremental`。`SPEEDUP5_CHECK` の1000一致と、`SPEEDUP5` のedit_to_capture_ms／full_description_ms／reused_fragments／serialized_fragmentsを比較。incrementalはtest専用です。

## 項目2b: 動く立ち絵

- 状態: 一部（PNG・付属INIなし・Sessionキー、立ち絵の表示区間のみ停止中先読み対象外）
- ブランチ・PR: `codex/speedup-2b-animation-tachie`、[PR #12](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/12)
- 変更: 現在のsession／task／取消slotと公開済み音量sampleのbit一致を確認。部分終了や時間切れfallbackを保存せず、親へ未完成を伝播。全候補PNG・番号／母音部品の一覧と実sourceのINI保持状態・parts countを同期確認。ホスト契約・全記録済み基準を更新しました。
- 検査: 8独立case（画素・非表示母音・timeout・残存INI・一覧変更・上書き・hook rollback・metadata上限）。別の計測で900frame×2のoff対cache全byte一致。[最終CI37244739478](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37244739478) 全通常job成功。
- 計測（CI、WARP。RTX3060の値ではない）: [同一job37244010366](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37244010366)、2体・20voice・60秒15fps・321×181、GPU保持off

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| 2回目hit | 0／900、0／900 | 900／900、900／900 | 再利用率向上・画素一致 | 100%、900×2画素一致 |
| 2回目時間 | 1.052230／0.748328 ms/frame | 0.827060／0.820715 ms/frame | 短縮 | 安定した前後改善は未確認 |
| 2回目／自身のoff | 1.055160／0.617762 | 0.837570／0.712560 | 正規化して短縮 | 約21%改善／15%悪化 |

- 計画からの変更点: まばたきはホストの結果を起動中のSessionキーで保存。INI・動画・差分合成・group・表情同一layer・入れ子を対象外。音量計算2枠を再生へ残すため、立ち絵の表示区間の idle 先読みを見送ります。表示区間外は複製から先読みし、追加した 6 フレームの検査で hit と全画素一致を確認しました。
- 残った危険・未確認: 初回保存費用、実機速度、安定した前後改善は未確認。新設画素試験のexport透明背景対preview黒背景という比較誤りはPRで先に説明して修正し、900全byte・readiness・30秒上限を維持。失敗commitは再実行していません。2c基準CIでも30秒上限に達したため、PR #13で診断し、hostの元のoutputがcold frameごとに残る解放漏れを修正。120frameのcacheオン/オフ全byte一致とcommand list非増大が成功し、900frame保存は4.42／3.24秒で完了しました（CI37250668285）。
- 利用者のPCで確かめてほしいこと: PNGのみ・INIなしで2回再生し「再利用」とtrace `timeline-update` Outcome `live/gpu/ram` を確認。`auxiliary-readiness` のComponent `lip-sync-published-value`、Outcome `ready/not-ready` を確認。未完成・部品追加・上書き後に保存されないか確認します。CLIは `SPEEDUP2B` のcache_hits／ms_per_frameと `SPEEDUP2B_PIXELS exact=true`。

## 項目2c: PSD立ち絵

- 状態: 一部（監査済み同梱版・root内の検証済み経路、Sessionキー）
- ブランチ・PR: `codex/speedup-2c-psd-tachie`、[PR #13](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/13)
- 変更: 共有PsdFileSettingsの実内容を、全体のJSON設定から独立したserializerでキー化。通知と通知なし変更、source内に残った古い正規化を確認します。読込済みPSDのbytesと保護したファイルを照合し、最初の指紋より前の上書きも拒否。非同期音量の未完成・部分失敗・取消とCPU合成失敗は保存しません。ホスト契約12規則と記録済み全版（4.56.1.0）の基準を更新しました。
- 検査: PSDの12独立STA/device・各30秒caseと、前後各2回の900frame全byte画素一致が成功。notify、非通知Offset／Layersの実画素反例、sidecar、上書き、timeout、部分失敗／取消、設定上限、CPU合成失敗と回復、指紋前上書き、全体JSON converterへの耐性を含みます。既存Animationには120frameのoutput解放検査を追加し、既存検査も全件維持。[実装最終CI37262111837](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37262111837) 全通常job成功。文書commitを含む最後のCIはPR #13に記録します。
- 計測（CI、WARP。RTX3060の値ではない）: 上記同一Windows job111611365274、前 `0b10e2623fd30cc90c2d03cb10e77677eca4e65a`／後 `b5ee674a713590597b66fccc4fb8563a4d35147c`。2体・20voice・60秒15fps900frame・321×181、自作7層80×100 RGBA PSD、目／口各3、通常のまばたき・MouseSmooth4。RAM256MiB、GPU保持off、disk0。Update＋黒背景viewportへのDrawを測り、画素読戻し・reference・初回保存は2回目の時間から除きます。各sampleは別STA/device・30秒上限です。

| 対象 | 前（2回） | 後（2回） | 合格基準 | 判定 |
| --- | --- | --- | --- | --- |
| off時間 | 2.256610／1.650579 ms/frame | 1.843970／1.431048 ms/frame | 正規化の基準 | runner差を考慮 |
| 2回目hit | 0／900、0／900 | 900／900、900／900 | 再利用率向上 | 100%、GPU hitは0 |
| 2回目時間 | 1.619477／1.447797 ms/frame | 1.164649／0.859083 ms/frame | 短縮 | このrunは両回短縮 |
| 2回目／自身のoff | 0.717659／0.877145 | 0.631598／0.600318 | 正規化して短縮 | 前後約12.0%／31.6%短縮 |
| 900frame画素比較 | 正常描画同士、各900一致 | off対cache、各900一致 | 全byte一致 | 達成 |
| afterの初回900保存 | 対象外 | 4009.8563／2670.9473 ms | 30秒以内 | 達成、2回目時間に含めない |

- 計画からの変更点: まばたきは起動中のSessionキーでホスト自身の結果を保持。音量計算2枠を再生へ残すため、PSD 立ち絵の表示区間の idle 先読みは対象外。表示区間外は先読みします。sidecarの外部変更がホストの共有設定へ反映されない場合は、実際の共有オブジェクトを入力とします。未知module・group・入れ子・表情同一layer競合は拒否。実値の bounded な検査を維持し、設定 JSON は通知世代と全値の witness が一致する場合に再利用します。assembly の監査は新しい AssemblyLoad の世代でやり直します。読込済み bytes の SHA は背景で計算し、完了までは通常描画です。PSD前のCIで見つかった元のhost output解放漏れをPR #13で修正しました。
- 残った危険・未確認: afterは自身のoffより約36.8%／40.0%短い一方、直前の別runでは正規化が約2.5%悪化／31.5%改善でした。最新2回だけで安定した高速化を保証しません。当初は小さい自作 PSD の計測でした。レビュー対応では生成した 109 MiB PSD と 300 個の設定も測定しました。実プロジェクトの多様な PSD、PSB、RTX3060、GUI 音声／Present、長時間使用は未確認です。上書きやsource設定不一致は再起動まで対象外になり得ます。誤った新設fixtureの修正理由は先にPRへ記録し、結果文書にも残しました。同一の失敗commitの再実行はありません。
- 利用者のPCで確かめてほしいこと: PSD2体・20voiceを2回再生し、「再利用（同じ画像／GPU／RAM／ディスク）」「新規描画」「対象外」とp50/p95を比較。trace `timeline-update` のOutcome `live/gpu/ram`、`auxiliary-readiness` のComponent `lip-sync-published-value` とOutcome `ready/not-ready` を確認。目／口設定の変更・素材上書き・未完成描画が古い画面を保存しないか確認します。CLIの `--psd-tachie-check`／`--psd-tachie-measure` は `SPEEDUP2C` のcache_hits／ms_per_frame、`SPEEDUP2C_PIXELS exact=true`、`SPEEDUP2C_PHASE` のcold_warm_msを確認します。


## まとめ

- 製品化: 1、2aの限定範囲、6、8、2b・2cの限定範囲。4は通知遅延で画素一致を守れず中止。5は内容一致した試作でも10ms台未達のため製品導入を中止。2a・2b・2cは対応範囲と実機未確認を含め「一部」と報告。
- 先行レビュー修正 [PR #5](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/5): layer順の曖昧性、採用直前検証、disk keyと画素checksum、計算待機cycle、providerのslot同一性、Session保存前検証、HEVC設定のportable検査。mainの進展を起点に修正済み。[最終CI37186362385](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37186362385) 全通常job成功。Session先読みのpurge漏れは項目8で追加修正しています。
- マージの順番（利用者がレビュー後に行う場合）: **#14（main 起点の独立修正）→ #5 → #6（1）→ #7（4）→ #8（2a）→ #9（6）→ #10（8）→ #11（5）→ #12（2b）→ #13（2c）**。#14 は main 起点の独立差分、#5〜#13 は main 宛の累積差分です。#14 の修正は #10 / #13 にも含まれるため、先行マージ後の差分・重複を再確認してください。順に取り込んだ際は次PRの差分を再確認してください。こちらでは全件draft・未マージを維持します。
- 次にやるとよいこと: Claudeで各項目の境界とhost契約・採用直前検証をレビュー。利用者のRTX3060 PCで同じfixtureと実プロジェクトを測り、GUI応答・ドライバー・OS予算を確認。4／5を再開するなら、通知だけの安全性を仮定せず、同期検証と全文処理の費用を先に分離して調べてください。2b・2cの初回保存費用と対応外の経路は、画素一致を維持する検査から追加してください。
