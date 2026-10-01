# Claude 引継ぎ — YMM4 RTX3060 NVENC / AE風キャッシュ

更新日: 2026-10-01（追記8まで）。これは **未完成の作業保存（WIP checkpoint）** です。製品完成・配布可能・AE完全再現を意味しません。

## 最初に読むこと

### 2026-10-01 追記（8）— 外部プラグイン・図形・フォントによる bypass をアイテム単位に

ユーザー:「外部プラグイン、外部図形、外部フォントが広い範囲でbypassされてしまう問題も修正しておいて」。1df5203。
- 以前: 外部の `$type` が 1 つ、外部アイテム型・外部図形・`ve://`/`ae://`/`plugin://` の resource（ユーザーの音声合成プラグインを含む）が 1 つでもあると、プロジェクト全体を通常描画。カスタム読み込みプラグインが有効で素材ファイルが 1 つでもあっても全体。フォントは WPF の `FontFamily(名前).GetTypefaces()` で探し、YMM4 の表示名（例「Arial Bold」や和名）が WPF のファミリー名と一致しないと解決できずそのアイテムが bypass。
- `FrameModelSplit`: `classify(type, path)` が `Known` / `AudioOnly` / `Foreign` を返し、`Foreign` は読み取り中の所有者（root アイテム i、他タイムライン、キャラクター j）に帰属。どれにも属さない（プロジェクト全体の設定、タイムラインの映像設定など）ときだけ model を拒否。モデル直下とタイムライン直下の `$type` も検査するようにした（以前は素通り。実際の snapshot には無い）。
- `FrameCacheKey.ClassifyType`: 本体と Plugin API 以外の型は Foreign。ただし `VoiceParameter`（音声ファイルを作るだけで、映像の描画コードは読まない: TimelineSource・JimakuSource、4.56.1.0）と `Tachie*Parameter`（立ち絵のフレームはキャッシュしない）配下は Known、`AudioEffects` 配下は AudioOnly（音声を読む wide フレームだけ）。同梱 Community のエフェクト・図形（OpenFx、VST3、音量連動など中身を確認していないもの）は Foreign のまま＝そのアイテムのフレームだけ通常描画。
- `ClassifyResource`: `ve://`・`plugin://` はユーザープラグインだけに付く（`TimelineResource.TryCreateFrom*`）。音声合成・立ち絵プラグインは Known、`ae://` は AudioOnly、ほかは Foreign。
- アイテム: 外部アイテム型・外部図形（`ShapeType2`）・Foreign resource・Foreign キャラクター・カスタム読み込みでの素材ファイル（フォントは除く）・列挙の例外 → そのアイテムだけ uncacheable（`FrameDependencyIndex` が映るフレームとトランジション・wide を追う）。他タイムラインなら `nestedUncacheable`。キャラクター: 例外・Foreign resource・カスタム読み込みでの素材ファイル → そのキャラクターのアイテムだけ。
- フォント（`ResolveFont`）: YMM4 と同じく `SystemFonts.Concat(CustomFonts)` から `FontName` 一致の最初、無ければ `new Font()`（Arial / 400 / Normal / Normal）。その `CanonicalFontName` を DirectWrite のシステムコレクションで `FindFamilyName`、ファミリーの全フォントの `CreateFontFace().GetFiles()` を `IDWriteLocalFontFileLoader` でパスにする（太字・斜体は同じファミリーの別 face を選ぶため全ファイル）。resource に `fontface://名前\nファミリー|太さ|スタイル|幅` を追加（他タイムラインの resource も nested hash に入れるようにした）。ファミリー単位で 30 秒保持。DirectWrite に無いファミリーと、ローカルでないフォントファイルは NotSupported → そのアイテムだけ。`KeyDependencyTracker` は `FontSettings`・`CustomFonts`・各カスタムフォントを購読。
- 追跡しないもの（README に記載）: DirectWrite の代替フォント、YMM4 がフォント一覧を再読込せずにファミリーへ face が追加された場合（次の記述まで）。
- あわせて（a670740、キーの漏れ）: YMM4 はテキストと字幕の文字を描画前にユーザー辞書の「*」置換（`UserDictionary.AsteriskWordSets`、`TextHelper.ApplyAsterisk`）で書き換えるが、この設定がキーに無く、置換を編集してもキャッシュの古い文字が出得た（描画中に読まれる設定を `SettingsBase<` で列挙して見つけた。ほかの YMMSettings・FileSettings・FontSettings は対応済み）。有効な置換を TextItem・VoiceItem の resource `asterisk://<hash>` に（`UserDictionary` は internal なので reflection。読めなければそれらのアイテムだけ uncacheable）。tracker は辞書と各 WordSet を購読。文字装飾（`Decorations`）・制御タグ `<@名前>`（`ControlTagParser.Parse`、生の文字と各置換先の文字を解析する上位近似）のフォントは、フォント設定にある名前だけ `ResolveFont`（無い名前はアイテムのフォントで描かれる: TextSource・JimakuSource）。CacheChecks（タグのフォントの対応追加で key が変わり戻る、置換の追加・編集で文字のフレームだけ key が変わり戻る）。
- 試験: StoreChecksHarness の model split（400 モデル、各 `$type` の所有者を parse した木から求めたものと一致。タイムライン設定を items の前後に置く。変異 4 件で検出）。CacheChecks（外部エフェクトの派生型・外部アイテム型でそのフレームだけ bypass、他タイムラインの外部エフェクトはシーンアイテムのフレームだけ、カスタム読み込みは素材のあるフレームだけ、未知のフォント名は Arial として key、カスタムフォントの追加・太さ変更で key が変わる、DirectWrite に無いファミリーはそのフレームだけ）。
- あわせて（14f71ad）: idle 先読みが、常に通常描画になるフレーム（立ち絵・外部コード・確認できない素材）で止まり、次の idle でも同じフレームから始めて先へ進まなかった。`KeyDependencyTracker.RendersNormally(frame)`（現在の記述でそのフレームが uncacheable。編集で false に戻る）で、そのフレームを飛ばして続ける（素材の確認待ちなど一時的な理由では従来どおり止まる）。範囲がすべてそうなら `NextFrame` を範囲の後へ進める。CacheChecks で判定を確認。RenderBatch 全体の動作は GUI では未確認。
- CI: verify run 21（1c61043）全ステップ成功（CacheChecks の外部エフェクト・外部アイテム・他タイムライン・カスタム読み込み・フォント解決/別名/存在しないファミリー・`RendersNormally`、`--integration`、HostCacheProbe）。run 20 は試験側 2 件（理由文の語、run 19 と同じ切り替え試験）と、古い workflow を使った同期の誤り（`--integration` なし）で失敗。gui-smoke run 12（1c61043）: 実 YMM4 で同梱 Community の数値図形（2〜3 秒、Layer 03）のところだけ帯が空き、先読みはその先の 10 秒まで到達（「先読み範囲（10秒分）に到達しました」、新規描画 268・対象外 10）、Arial の文字も保存。
- あわせて: verify run 19（8a8a1b5）の失敗は試験側の誤り（出力フレームは前の試験で保存済みで、出力キャッシュのみ有効時に 2 回とも hit）。各計測を空のキャッシュから始めるよう修正（8b6b871）。

### 2026-10-01 追記（7）— NVENC 出力とキャッシュの設定を分けた

ユーザー:「NVENCを使うものと、YMM標準のプレビューのままキャッシュを使うものを設定で分けれるようにしといて」。8a8a1b5。
- `FrameCacheToolSettings`（設定ファイル名は従来どおり `NVEncVideoWriterPlugin.FrameCacheToolSettings.json`）に 3 つの切り替え: `PreviewCache`（YMM4 標準のプレビューのままキャッシュ。アイドル時の先読み・帯を含む）、`ExportCache`（どの出力形式でも動画出力の描画をキャッシュ）、`NvencOutput`（出力形式「RTX 3060 NVENC 出力」）。`SettingsVersion` 0 のファイルは旧 `Enabled` を両方のキャッシュへ引き継ぐ。新規は NVENC のみ有効。
- UI: ツール「描画キャッシュ」と YMM4 の設定（その他 > RTX 3060 NVENC・描画キャッシュ）に同じ 3 つのチェックボックス（`PluginSettingsPanel`、`FrameCacheToolSettings.Default` に双方向バインド）。変更は `PluginSettings` がその場で反映し保存する。
- `TimelineFrameCache.SetEnabled(preview, export)`。`Enabled` は「どちらか」（テスト用の setter は両方）。Update・保存・idle の prime・帯・idle 先読みはそれぞれの用途のスイッチを見る。
- NVENC: 起動時に無効なら `HostExportScope` のフックを入れない（`HostIntegration.ExportHooked`）。後で有効にしたら `PluginSettings` がその場で入れる。無効のまま NVENC 形式で出力すると `RequireExportScope` が理由つきで中止、設定画面にも同じ文言。出力フックの失敗は NVENC 出力だけを無効にし、キャッシュは続ける（以前は全体を無効化）。
- 試験: HostCacheProbe（プレビューだけ・出力だけの切り替えで、もう一方が再利用されないこと。旧・新・現行の設定ファイルの読み込み）。YMM4 の設定画面の表示は GUI では未確認。

### 2026-10-01 追記（6）— ChatGPT の v2 引継ぎ（`docs/AE_YMM4_Cache_v2_Handoff_2026-10-01.md`）への対応

ユーザー:「docs/AE_YMM4_Cache_v2_Handoff_2026-10-01.md にそろそろ ChatGPT から評価が来ると思うので、それをもとに作業を継続して」。評価（ユーザーが f5098bf で追加、解析対象は 8531425）を HEAD 3508439 と照合し、P0 → P1 → P2 の一部を実装しました。

**HEAD での照合結果（v2 の表 5 / 6 章）**
| v2 の指摘 | HEAD 3508439 の状態 | 対応 |
|---|---|---|
| `FrameTimeKey` が境界 ±1 ms を同じキーに | 残っていた | 正確な ticks をキーに（`t{ticks}@{fps}`、schema `pixels-v7`）。4.56.1.0 の描画元（再生・一時停止・`VideoFileWriter`・現在フレーム出力）と先読み・帯はすべて `VideoInfo.GetTimeFrom(frame)` で時刻を作るので再利用は減らない |
| `GetResidency` と disk worker の `_disk` 競合 | 残っていた（worker は lock なしで変更） | 索引の変更は worker が `_gate` 下で行い、file I/O 中は持たない。他スレッドは `_gate` 下で読む。purge 永続化中のフレームは帯に出さない |
| `TryGet` の RAM miss は読み込み予約のみ | 残っていた | 読み込みは書き込みより先（`ReadsFirst`）。一時停止の要求は最大 50 ms 待つ（描画は YMM4 の描画タスク上で UI スレッドではない）。再生中は先読み（`Prefetch`） |
| 通常プレビューの完成画素を保存しない | 残っていた（保存は idle と出力だけ） | 再生・一時停止・シークで描画したフレームを保存。GPU readback は次の Update か player の `Edit()`（描画スレッド）で仕上げ、描画スレッドは GPU を待たない |
| idle が保存済み・計算中を skip しない | 残っていた | 保存済み（RAM・ディスク）は描かない（`IsPreviewStored`） |
| 背景 fingerprint が `cachedPaths` 全体 | 残っていた | chunk ごとに公開し、待っているフレームのファイルを先頭の別 chunk に。検査中でも自分のファイルが確認済みのフレームはキーを作る |
| `TachieItem` 等が全体 bypass | 残っていた | 立ち絵は映るフレーム（と、それを含むタイムラインを描くシーンアイテムのフレーム）だけ通常描画。同梱の立ち絵プラグインの parameter 型は許可。外部アイテム・外部図形・外部エフェクトは従来どおり全体 bypass（未知の global 依存として保守的に） |
| whole-project JSON → item 分解 | 残っている | 未着手（未計測。下の「次の課題」） |
| SingleFlight（同一 key の計算共有） | 残っている | 未実装。重複は「idle の計算中に同じフレームをライブが要求」だけで、idle は操作で中止され結果は捨てられる（1回の割り込みにつき最大1フレーム）。出力とプレビューは usage がキーに入るので互いに合流しない |
| viewport を含む preview key | 維持 | 変更なし（v2 も維持を指示） |

**実装（commit）**: 96ec9ba（P0・P1 本体）、c1c61c0（保存の capture が Harmony の finalizer で破棄されていた不具合）、05d1df2（立ち絵）、1f69a6d（フレーム単位の素材準備）、22654dd（先読み窓が RAM 内のフレームを数えずに自分を追い出していた不具合）。v2 の「クラス設計のたたき台」は新設せず、既存クラスに入れました（`FrameCacheStore` = FrameDelivery の読み込み・先読み、`TimelineFrameCache` の `DeferredStore`/`PreviewReadback` = 保存、`KeyDependencyTracker` = FileFingerprintService 相当）。

**計測（経路別）**: ツールの「描画キャッシュ」に、再利用の内訳（同じ画像 = live reuse / RAM / ディスク）、新規描画（host render）、対象外（bypass）、プレビュー保存数と描画スレッドでの時間（ms/枚）、先読み読込数、ディスク読込数と ms/枚、書込数、混雑で見送った書込数を表示。`render_ms`（host の描画時間）そのものは分けて計っていない（新規描画の回数のみ）。

**検証（CI、Windows Server 2022 + YMM4 4.56.1.0、GPU は WARP）**
- verify run 15（0ebc113）全ステップ成功。主な出力:
  - StoreChecks: 索引の並行 stress（3 秒で約 68 万操作、例外なし、終了後の会計とファイル数が一致）、ディスク供給・先読み窓。HEAD 3508439 の store に同じ stress（Linux、3 秒 × 5 回）をかけても例外は出なかったので、この試験は競合の再現ではなく回帰防止です（Dictionary の非同期読みは多くの場合、例外でなく誤った答えになる）。
  - CacheChecks: 同梱の立ち絵（SimpleTachie の `CharacterParameter`）で立ち絵の無いフレームはキー化・立ち絵のフレームは通常描画。フレーム単位の素材準備: 2 MiB × 150 ＋ 小さい 1 ファイルで、そのフレームのキーは 538 ms（大半は 151 アイテムの描画状態の記述）、全ファイルの検査完了は 889 ms。
  - HostCacheProbe（実 host の `TimelineSource`、player は stand-in）: 1 周目（idle 先読みなし）host 描画 30・保存 30、RAM 8 / ディスクのみ 22。2 周目（30 fps で再生）host 描画 1・ディスク供給 29（ディスク読込 0.89 ms/枚）。一時停止でディスクのみのフレームへシーク: 描画 0・ディスク供給 1・選択枠を復元。1 アイテム編集: 無関係なフレームは描画 0、Undo で描画 0。再起動後: 一時停止のシーク 描画 0、再生 19 枚 描画 0・ディスク供給 19。すべて host 描画と画素一致。Update の所要時間 p50/p95（WARP、試験の図形）: 新規描画 0.8/12.2 ms、ディスク 0.6/1.3 ms、同じ画像 0.0/0.2 ms、保存は描画スレッドで 0.75 ms/枚。実動画で境界の 1 tick 前: host の sample は同じだった（この動画では差が出ない）が、キャッシュ ON/OFF の画素は両時刻で一致。
- gui-smoke run 9（f98d131、実 YMM4 を起動）: 20 秒のプロジェクトで、idle 先読み（10 秒）を越えた 16 秒へシークして即再生 3 秒 ×2。ツールの表示（写真）: 1 回目の後「再利用 8（同じ画像 6 / RAM 1 / ディスク 1）/ 新規描画 455 / プレビュー保存 94（描画スレッド 4.9 ms/枚）/ 先読み読込 4」、2 回目の後「再利用 114（同じ画像 21 / RAM 2 / ディスク 91）/ 新規描画 474 / プレビュー保存 97（5.0 ms/枚）/ 先読み読込 108 / ディスク読込 110（5.2 ms/枚）/ 書込 482（見送り 0）」。2 回目はほぼディスクから供給（新規描画の増分 19 には idle 先読みを含む）。player の back buffer はプロジェクトの解像度（1280x720、1 枚 3.7 MB）なので RAM 256 MiB は約 70 枚で、3 秒の再生（90 枚）は RAM に収まらない。所要時間 p50/p95: 新規描画 1.7/8.3 ms、ディスク 3.3/4.5 ms（この軽いプロジェクトでは中央値はディスクの方が遅い。新規描画の時間は `TimelineSource.Update` のコマンドリスト構築だけで、GPU での実行（player の Draw）を含まない）。
- 新たに分かった問題: **描画状態の記述（`FrameCacheKey.TryDescribe`）が 1 アイテム約 2 ms**（100 図形 250 ms、1000 図形 1978 ms、1000 文字 1803 ms、3000 混在 6637 ms、5000 で上限超過→全体 bypass）。編集のたびにプレビューの描画スレッドで実行されていたため、1000 アイテムのプロジェクトでは編集ごとに約 2 秒プレビューが止まり得た。対応:
  - f393928 / 6519082: プレビューの描画スレッドは、200 アイテム以下か直前の記述が 50 ms 以下のときだけその場で記述し、それ以外は背景タスクで記述（その間は通常描画、完了後に revision と読み込みプラグインが変わっていなければ採用）。CI: 1000 アイテムで最初の capture は 1.8 ms で戻り、キーは約 1 秒後。出力・idle 先読みはその場で記述（待ってよいスレッド）。
  - e7892a4: 記述の深いコピー 2 回を削除、3394b33: フォントの解決を 30 秒保持。CI run 16: 100 図形 250→73 ms、1000 図形 1978→891 ms、3000 混在 6637→2564 ms。
  - 内訳（run 16、1000 図形）: GetFiles 0 ms、GetResources 1 ms、YMM4 の JSON 化 218 ms、parse 553 ms、`$type` 走査 220 ms、アイテムごとの再シリアライズ 131 ms。
  - 8a4a2e9: parse・`$type` 走査・分割を JsonTextReader の 1 回の走査に（木を作らない）。出力文字列と拒否する型は従来と同じ（StoreChecks: 生成した 300 モデル、うち 80 は外部の型で拒否）。Linux の 826 KiB モデルで 225→18 ms。CI での値は次の run で確認。
  - 上限（`MaximumModelCharacters` = 1600 万文字）は据え置き。5000 アイテム（23 MB）では超えて全体 bypass になる。木を作らなくなったので上げられるが、`cachedModel` を tracker ごと（プレビュー・idle・出力）に保持し、idle の複製はこれを読み込むため、上げる前にメモリを測ること。

**実装上の注意（次の Claude 向け）**
- Harmony の finalizer は例外が無くても毎回走ります。`Update` の `Finalizer` が `Pending.Dispose()` を呼ぶため、プレビュー保存で capture を次の Update まで持ち越すときは `Pending.HandOver()` で引き渡し、`DeferredStore.Dispose` が `Release()` します（c1c61c0 まで、保存したフレームが全て「古い」と判定されて 0 枚だった）。
- 先読み窓（`Prefetch`）は RAM 予算の半分。窓内で既に RAM にあるフレームも数え、最近使ったものへ移します。数えないと窓が RAM を超え、読んだフレームを表示前に自分で追い出します（22654dd まで: 30 フレームで 91 回読み、12 回描画）。
- 一時停止中の保存: `refreshSupported`（4.56.1.0 と同じ player のコード）なら `BeforeEdit`（player のループ、描画スレッド）で仕上げます。無い版（4.55.1.1 など）は、次の Update が来ないかもしれないので、その場で GPU を待って読みます（一時停止・シーク時に readback 1 回分の待ちが増える）。
- `TimelineFrameCache.UseStore` / `TestViewport` / `CompletePendingStore` はテスト用（HostCacheProbe の `PreviewDeliveryChecks`）。player 実物の swap chain を作らずに、プレビューの保存・ディスク供給の経路を実 host の `TimelineSource` で通すため。
- 立ち絵: `FrameCacheKey.IsBundledTachieParameter` は YMM4 のフォルダーから読み込まれた `YukkuriMovieMaker.Plugin.Tachie.*` の型で、JSON 上の祖先に `Tachie*Parameter` プロパティがあるものだけ許可。キャラクターの立ち絵ファイルは依存から外しますが、`Character.GetFiles()` が「字幕・音声エフェクト」+「立ち絵の 5 部分」と一致しない版では全ファイルを依存に戻します。

**未実行・未確認**
- ユーザーの PC（RTX 3060、YMM4 4.55.1.1）、NVENC 出力、実プロジェクトでの速度（CI は WARP、GPU readback の待ち・PCIe 転送は実 GPU と別物）。
- 実 GPU での「描画スレッドが GPU を待たない」ことの確認（遅延 readback の効果は WARP では測れない）。
- UI 操作から表示までの p50/p95、frame drop（計っているのは Update の所要時間）。
- 動画素材を含むプロジェクトでの 2 周目・再起動（CI は図形のみ。動画は境界時刻の画素一致だけ）。
- VFR 動画、disk full、device loss の試験（コード上はいずれも通常描画へ戻る経路）。

**次の課題（v2 の残り）**
1. 実 GPU で計測（ユーザーの PC か GPU のある runner）: ツールの「描画の所要時間 p50/p95」と保存の ms/枚。保存が重ければ、host の出力を一度だけ描いた bitmap を player に渡す方式（1 回描画＋ blit）を検討。
2. whole-project JSON の差分更新: まず CacheChecks の「Describe after an edit」の数字（CI）で、編集後の待ちの何割かを確認してから。
3. SingleFlight: idle 中断時の重複（最大 1 フレーム）以外に重複が出る経路が見つかったら。
4. 外部エフェクト・外部アイテムの限定許可（固定版 adapter）、canonical scene raster、item/effect cache、MFR は v2 どおり後段。


### 2026-10-01 追記（5）— 実 YMM4 の画面で見つけて直した不具合

ユーザー:「今のUIをスクショ撮ってみて欲しい」「スクショ取れるまで止めないで。全画面表示とかもね」。CI の YMM4 コピー（release 0.1 の 4.56.1.0、ユーザーの PC ではない）を GUI で起動し、最大化した画面写真を撮りました（`tools/ci/gui-smoke.ps1`、下の GUI 起動テスト）。最終の run 8 で: ツール「描画キャッシュ」に「先読み範囲（10秒分）に到達しました」、ツールとルーラーの帯が 0〜10 秒すべて埋まる（古い分は青=ディスク、新しい分は緑=RAM）、5 秒へシークすると四角 2 つと文字のフレームが出て、先読みが 151 フレームから再開。GPU は runner の基本アダプター（WARP 相当）で、RTX 3060 での見た目・速度は未確認。

写真で見つかり、直した不具合（どれも以前の単体テストでは出ていなかったもの）:
1. **同梱の MIDI 読み込みでキャッシュ全体が止まる**: `IsBuiltInSourceReader` が `YukkuriMovieMaker.Plugin.FileSource.*` だけを組み込み扱いにしていたが、YMM4 は `YukkuriMovieMaker.Plugin.Community`（`MidiAudioSourcePlugin`）も既定で読み込むため、素材ファイルやフォントを使うプロジェクト（文字アイテムだけでも）は常に「外部素材でカスタム読み込みプラグインが有効」で通常描画になっていた。YMM4 のフォルダーにある `YukkuriMovieMaker.Plugin.*`（`PluginAssemblyLoader` の既定一覧と同じ場所）を組み込み扱いに。`user\plugin` の同名は外部のまま。CacheChecks が実 host の全読み込みプラグイン型（13 種）を確認。
2. **アイドル時の先読みが実プロジェクトで一度も働かない**: 複製（`CloneScene`）が `Timeline.Length` を写していなかった（`Items` を設定しても長さは 1 のまま。YMM4 は読み込み・編集時に private setter で更新）ため、描画状態が live と一致せず「ライブ状態と複製状態が一致しない」で毎回中止。live の値を setter で写す。HostCacheProbe の複製テストを長さ 94 のタイムラインに。
3. **素材を使うフレームで先読みが止まる**: 複製のトラッカーがバッチごとに新規作成され、素材（フォントを含む）のハッシュが終わる前に破棄されていた。live のトラッカーの検証済み指紋を引き継ぐ（`KeyDependencyTracker(scene, verified)`、毎回 lease で stamp を照合）。
4. **（キャッシュの正しさ）ファイル更新後に古いキーが通り得た**: lease は共有の指紋索引で新しい stamp を受け入れるが、キーはトラッカー自身の古い指紋で作っていた。別のトラッカー（出力・別プレビュー）が先に新しい内容を確認すると、古い画素を出し得た。lease の指紋がキーの指紋と同じときだけ capture を返す。CacheChecks に「別トラッカーが先に検証」ケース。
5. **先読みが自分で自分を止める**: WPF は配置が変わり得るたびに入力なしのマウス移動を発生させ、帯の再描画（フレームが保存されるたび）で `PreProcessInput` → 「操作を検知」→ ジョブ中止を繰り返していた。キー・文字・ボタン・ホイール・ペン/タッチと、カーソル位置が実際に変わったマウス移動だけを操作とみなす（`IsUserInput`）。
6. **帯が一部しか出ない**: 帯はプレビュー側トラッカーの `TryPeekFrameKeys` を使うが、そのトラッカーはプレビューが素材のあるフレームに来るまで素材を検証しないので、先読み済みでも文字のフレームが帯に出なかった。表示時にも背景検証を始める（`AdoptFingerprints`/`StartFingerprinting` に整理）。CacheChecks に別トラッカーの保存を帯が表示するケース。
7. 先読みは最後のプレビュー描画から 10 秒（`TryPrimePreview` は 30 秒）で止まっていた。プレビューは変化が無いと再描画しないため、重いフレームでは 10 秒分の先読みが終わらない。`IdleViewportLifetime` = 2 分に（仕事量は再生位置から 10 秒分で上限があるので増えない）。

検証: verify run 8〜10（CacheChecks、HostCacheProbe --integration / --gpu を含む）と GUI run 8 が成功。

残り（次の Claude 向け）:
- 先読みは保存済みのフレームも描き直す（`RenderBatch` は保存の有無を見ずに `source.Update`）。シークで戻ったとき、ディスクにだけあるフレームを描き直している（新規描画 296→368）。RAM にあれば飛ばす・ディスクだけなら読み込み要求にする、が候補。キーは `TryPrimeCore` と同じ組み立て（`MakeKey`、usage の ShowOnlyPreview、device context）が要る。
- 写真では選択したアイテムの選択枠がプレビューに見えていない（YMM4 がマウスをプレビューに乗せたときだけ描く可能性。未調査）。
- ユーザーの PC（4.55.1.1、RTX 3060）では GUI 未確認。この版は `HostFeatures` で preview のみ。

### 2026-10-01 追記（4）— YMM4 の版が変わっても止まらないようにする

ユーザー:「バージョンが変わったら全部動かなくなってしまう可能性があるのは不便」。従来は本体・Plugin・Settings の MVID/SHA-256 が KnownHosts と一致しないと、キャッシュだけでなく **NVENC 出力まで開始しない**作りでした。

**出力（`HostIntegration.Install`）**: どの版でも `HostExportScope` の構造契約（`VideoFileWriter.CreateFileAsync(ProgressMessage, CancellationToken)`、`scene`/`settings`、`EncodeFrom`/`EncodeTo` の型）が合えば出力フックを入れます。予定フレーム数・取消の判定で完成ファイルを置き換えないので、中身が変わっても既存ファイルは壊れません（意味が変わった場合は「出力されない」側に倒れます）。

**キャッシュ（`HostFingerprint.cs`、`HostContracts.cs`、`HostBaselines.cs`、`HostFeatures.cs`）**
- `HostFingerprint`: PE を System.Reflection.Metadata で読み、型ごとに「基底・interface・フィールド・シリアライズ属性・全メソッドの IL（トークンを名前に、分岐先を命令番号に、コンパイラ生成の番号を # に）＋コンパイラ生成の入れ子型」を SHA-256 にします。読み込み・実行はしません。4.56.1.0 全 5146 型で約 2.5 秒、決定的。
- `HostContracts.Rules`: 機能ごとの「前提にしている本体の部分」= 明示した型（`Type`、`Type+`=入れ子込み、`Type::Method`=そのメソッドだけ）＋ **witness**（その機能の規則に関わることをするコードの全型）。witness は 4.56.1.0 を読んだときの grep を機械化したもの: `TimelineSourceUsage` を参照する型（usage で描画が変わるコード）、`Timeline(Item)SourceDescription::get_Scenes`（他シーンを読む）、`Player.Audio.Items.SceneSource`、`IItemPicker`、`GetFiles/GetResources` の定義、描画系 namespace から `YukkuriMovieMaker.Settings.*::` の参照、`VideoController/VideoEffectController::.ctor`、`CreateVideoFileSource` など。対象 assembly は YukkuriMovieMaker と YukkuriMovieMaker.Plugin（FrameCacheKey がそれ以外の `$type` を bypass するため）。デコーダーは assembly 単位（FFmpeg / MediaFoundation / WIC）。
- 機能: `core`（キャッシュ全体）、`preview`（TimelineVideoPlayer）、`selection-rects`、`wrapped-sources`、`ruler-bars`、`decoder:<assembly>`。`Requires` の依存つき。
- 未確認の版では起動時に `HostContracts.Describe(本体フォルダー)` → `Evaluate`（記録済みの版のどれかと core が一致すれば、その版との差が無い機能だけ ON）。判定は `%LOCALAPPDATA%\YMM4-RTX3060-NVENC\host-contracts.json` に（本体 DLL の MVID 群＋プラグイン MVID をキーに）保存。Windows CI で初回 1.6 秒、2回目 8 ms。
- `HostFeatures.For(host)`: 4.56.1.0 の MVID なら全機能、それ以外の KnownHosts（4.55.1.1）は従来どおり（preview のみ、デコーダーは形で判定）、照合で決めた版はその結果。`TimelineFrameCache`（preview/rects）、`FrameRenderReadiness`（wrapped、デコーダー）、`TimelineCacheBars` は MVID 比較をやめてこれを見ます。
- 記録の更新: `dotnet run --project tools/HostFingerprint -- emit NVEncVideoWriterPlugin/HostBaselines.cs <版>=<本体フォルダー>`（渡さなかった版は保持。**Rules を変えたら全ての記録済みの版を渡し直す**こと）。`contracts <dir>` で内訳と判定、`compare <旧> <新>` で版の比較、`members <旧> <新> <型>` でメソッド単位の差。

**実データ（CI の host-versions、公式更新サーバーの 4.54.0.0〜4.56.0.1 と release の 4.56.1.0）**: 連続する版の比較で core が一致したのは 4.55.0.0→4.55.0.1 と 4.56.0.0→4.56.0.1。4.56.0.x→4.56.1.0 の core の差は `ItemEx` だけだったので、`ItemEx::Contains`（フレーム範囲の判定）だけに絞りました。4.56.1.0 は versionlist2.php には無いが `Application Files/YukkuriMovieMaker_4_56_1_0/` から取得でき、release の zip と全機能一致。

**キーの漏れ（witness で発見、修正済み）**: 背景画像・テクスチャ・画像ブラシ（`Player.Video.Effects.BackgroundImageEffect`/`TextureEffect`、`Brush.BitmapBrushSource`）は `FileSettings.FileExtensions.GetFileType` で動画/画像を決めますが、この設定がキーに無く、拡張子設定を変えると古いフレームが出得ました。`FrameCacheKey` の snapshot に `FileTypes` を追加（Format 2）、tracker が collection と各要素を監視。CacheChecks に検査を追加。同じ調査で、`OutlineEffect` の `IsAviUtlOutlineEffect` と `VideoItem` の `IsFixedFpsEnabled` は exo 出力専用で描画には無関係と確認。

**新しい版の自動確認（`.github/workflows/ymm4-watch.yml`、main に取り込まれてから有効）**: 毎日 06:17 JST に versionlist2.php を見て、未報告の版があれば `tools/ci/fetch-ymm4.sh`（YMM4 自身の更新手順: `YukkuriMovieMaker.json` のハッシュを照合）で Actions cache に取得 → contracts 判定 → Windows でプラグインのビルド・CacheChecks・`HostCacheProbe --unread --gpu`（照合で ON の機能だけ画素一致などを検査）→ issue「YMM4 <版> の確認結果」を作成。`ci/ymm4-watch` への push は試行（issue を作らず summary に出す）。YMM4-dlls の同名ブランチに `CI_WATCH_VERSION` を置くと版を指定できます。**試行（4.56.0.1 を未確認の版として）は全ジョブ成功**: 照合は 4.56.1.0 と core 一致（FFmpeg の assembly だけ `FFmpegAudioFileSource` の変更で不一致→FFmpeg の動画は保存しない）、プラグインのビルド、CacheChecks、`HostCacheProbe --unread --gpu`（画素一致・無効化・選択枠・MF のデコード失敗）すべて成功。取得は `--top`（アプリのフォルダー直下 404 ファイル、2 並列で約 2.5 分）。

**実 YMM4 の GUI 起動テスト（`tools/ci/gui-smoke.yml` / `gui-smoke.ps1` / `tests/GuiSmoke`、YMM4-dlls の `ci/gui-smoke` に push で実行）**: runner（Windows Server 2022、1600x900、英語 UI）で release 0.1 の YMM4 にプラグインを `user\plugin\YMM4Rtx3060Nvenc\` へ入れ、`user\setting\<版>\NVEncVideoWriterPlugin.FrameCacheToolSettings.json` で有効化し、生成したプロジェクトを開きます。初回の About と拡張子の関連付けの確認は YMM4 自身の設定（`YukkuriMovieMaker.Settings.YMMSettings.json` の `Version` と `IsYMM?AssociationChecked`）で出さず（出たら WM_CLOSE / UIA / `WM_COMMAND IDNO`）、最大化し、UI Automation でツールメニューの項目（名前は中の Text、親の MenuItem を Invoke）から「描画キャッシュ」を開いて右側へ移動、Layer 00 のアイテムを選択して放置、5 秒へシークし、各段階の画面写真を JPEG/base64 でログに出します（artifact の blob は cloud から取れないため）。ログ→画像は scratchpad の extract.py 相当で復元。スクリプトは Windows PowerShell 5.1 が ANSI で読むので ASCII のみ（日本語は `\uXXXX` を `[regex]::Unescape`）。

**新しい版で core が一致しなかったときの手順（次の Claude 向け）**
1. ymm4-watch の issue / host-versions（`TYPES` 入力で `members` 差分）で、どの型・メソッドが変わったか見る。
2. その版の DLL を読む（クラウドからは manjubox.net に出られないので、ユーザーに YMM4-dlls の Release へ zip を置いてもらうか、CI 上で ILSpy にかけて差分の型だけ確認する）。前提が崩れていないか、`HostContracts.Rules` の witness に新しい種類のコードが要るかを判断。
3. 問題なければ `emit` でその版を記録に追加し、`KnownHosts` には入れない（照合で動く）。前提が崩れていれば規則・キーを直してから記録。

### 2026-10-01 追記（3）— Windows CI、フレーム単位キー、キャッシュバー

**Windows CI（ユーザー承認済み）**: private repo `sabiasagimp4-ai/YMM4-dlls` の branch `ci/nvenc-verify` に、このリポジトリの commit 済み HEAD のコピーと workflow（`tools/ci/verify.yml`）を置き、push で GitHub Actions の `windows-2022` を動かします。YMM4 本体はその repo の Release `0.1` の zip（4.56.1.0）を実行時にダウンロードし、どちらの repo にも commit しません。更新は `DST=<YMM4-dlls の clone> tools/ci/sync-to-dlls.sh` → `git -C $DST push origin ci/nvenc-verify`。NVIDIA GPU は無いので NVENC 出力（ManagedSmoke / NativeSmoke）は対象外、Direct2D は runner の基本アダプター（WARP 相当）で動きます。Server-Media-Foundation は再起動なしで入りました。

CI で分かったこと・直したこと:
- **実機 4.56.1.0 でキャッシュ全体が接続できない不具合**: Harmony 2.4.2 は例外フィルター（`catch ... when` の中で `continue`）を含むメソッドを作り直せず（`Incorrect code generation for exception block`）、`DirectShowVideoFileSource.Update` へのフックが失敗して `FrameRenderReadiness` ごと無効になっていました。4.56.1.0 では描画に使う動画ソースは必ず `VideoFileSourceFactory.Create` で `CachedVideoFileSource` に包まれ、その wrapper のフックが「検証済みでない中身」を拒否するので、フックできないソースは unverified に降格して続行するようにしました（wrapper 自身がフックできない場合は従来どおり全体を無効化）。`MFVideoFileSource.RefreshCurrentFrameWithReload` も同じ理由でフック不可ですが、Update はフックできます。
- 4.56.1.0 の `PluginLoader` は `PluginAssemblyLoader.IncompatiblePluginAssemblies` を読むため、テストの loader スタブで空リストを入れるようにしました（init-only なので `AccessTools.StaticFieldRefAccess` で書く）。
- runner には日本語フォントが無く、TextItem の既定フォントが解決できず全体 bypass になっていました（テストは Arial を指定）。これを機に、確認できない素材（未インストールのフォント、外部 URL のファイル）は**そのアイテムが映るフレームだけ**通常描画にしました。
- 計測（runner、目安）: 依存ファイルの毎フレーム lease は 10/50/200 ファイルで 5.8〜7.6 / 26.6〜43.6 / 107〜168 ms（フレーム単位キー導入前の全ファイル方式）。ShapeItem だけの `TimelineSource.Update` は通常 0.37〜5.26 ms（実行ごとにばらつく）、キャッシュ再利用 0.03〜0.04 ms。実プロジェクトの速度ではありません。
- **run 5（YMM4-RTX3060-NVENC 8328077）で全ステップ成功**: NativeChecks、プラグイン build、StoreChecks（Windows 専用区間を含む）、ReadinessChecks、FileLeaseChecks、CacheChecks（フレーム単位キー、確認できない素材のフレーム限定 bypass を含む）、HostCacheProbe（実 host の export scope、idle clone、WARP での画素一致、実 host のキャッシュ hit/invalidation、選択枠、キャッシュバーの residency、実 MediaFoundation reader でのデコード失敗の非保存と復旧後の再利用）。

**フレーム単位キー**（`FrameDependencyIndex.cs`、`FrameCacheKey.DescribeFrames`、`KeyDependencyTracker.TryCapture(int frame, ...)`）
- フレーム f のキー = global（モデル JSON から全タイムラインの Items を除いたもの: 設定・reader・キャラクター・root の VideoInfo/LayerSettings/Length）＋ f を含む root アイテム（`IItem.Contains` と同じ半開区間）の JSON ハッシュ＋それらのファイル指紋。CompositeItemPicker が選ぶのはその部分集合（非表示を除く）なので安全側です。
- トランジション: `TransitionItemPicker(isBefore)` が `Frame - 1` のアイテムを描くので、その時点のアイテムも（再帰的に）含めます。
- シーンアイテムと AudioSpectrum を含むアイテム（JSON に "AudioSpectrum"）は他タイムラインや音声を読むので、そのフレームは「wide」= 全体キー（他タイムライン JSON を含む）。
- 4.56.1.0 の renderer を確認: 他シーン参照は SceneSource と AudioSpectrumShapeSource（Scene/Timeline 音声）だけ、別フレーム参照は TransitionItemPicker だけ。
- lease はそのフレームのファイルだけ。指紋は全ファイルを背景で 128 個ずつ（1回 4 GiB まで）、失敗したら1ファイルずつにして、確認できないファイルはそれを使うフレームだけ bypass。上限は 4096 ファイル。
- idle 先読み・TryPrime・Prefix はすべてフレーム単位。Linux 単体テスト（StoreChecks の FrameDependencyChecks、規則の変異5件を検出）と CacheChecks（Windows）で確認。

**キャッシュバー**（`CacheBarLayout.cs`、`CacheStatusBar.cs`、`TimelineFrameCache.TryGetPreviewResidency`、`FrameCacheStore.GetResidency/Version`）
- 緑 = RAM、青 = ディスクのみ。プレビューの現在の viewport・usage（`Playing`、ShowOnlyPreview がなければ `Preview`）で保存されたフレーム。
- タイムラインのルーラー（`TimelineScaleView`）下端に 3px の帯を追加（`EventManager.RegisterClassHandler` で Loaded を受け、Grid に `IsHitTestVisible=false` の子を足す。x = frame × `YMMSettings.TimelineZoom` / 100 − `TimelineViewModel.Viewport.Value.X`）。4.56.1.0 の MVID のときだけ。ツールの「描画キャッシュ」にはタイムライン全体の帯。
- 表示は lease なしのキー（`TryPeekFrameKeys`）で、ファイルが変わってから次に描画されるまでは古い表示になり得ます（表示専用）。1ピクセル列あたり最大4フレームを標本にし、列の色は標本の最小状態。
- idle 先読みの範囲を再生位置から 1 秒 → **10 秒**に拡大。RAM 256 MiB / ディスク 4 GiB の上限は変えていません（720p プレビューで RAM 約70フレーム）。
- **未確認**: 実際の YMM4 画面での表示（帯の位置・ちらつき・スクロール追従）。CI では residency の値までしか確認していません。

**ユーザーの PC で次に確認すること**（未保存プロジェクトを保存してから）
1. YMM4 を 4.56.1.0 に更新するのがおすすめです。選択枠の再利用、ルーラーのバー、DirectShow のフック不可への対処は 4.56.1.0 の内部を読んで確認したものなので、4.55.1.1 では無効（通常描画）になります。
2. `build.ps1 -Smoke`（NVENC を含む。CI では GPU が無いため未実行）。
3. YMM4 上で: 再生・一時停止・シーク時に選択枠が出るか、ルーラー下端と「描画キャッシュ」ツールの緑/青の帯が先読みに合わせて伸びるか、アイテムを1つ編集したときにそのアイテムの範囲だけ帯が消えるか。

### 2026-10-01 追記（2）— 一時停止中・プレビュー上マウス時のキャッシュと選択枠

ユーザー要望:「キャッシュ中でも選択枠とかが出るようにできませんか」。`PreviewRects.cs`（新規）と `TimelineFrameCache.cs` で対応しました。

- **キー**: Playing と Paused の描画差は `ShowOnlyPreviewEffect` だけです（立ち絵の口パクは元々対象外）。モデル JSON にその名前が無ければ両方とも usage キー `Preview` を使い、あれば分けます（緩い文字列一致で fail-closed）。キー版は `pixels-v5`。
- **選択枠（`TimelineItemRects`）**: host が NeedRects 付きで通常描画したフレームの rects を、controller を配列化して `PreviewRects`（source ごと、LRU 512）に保存します。キャッシュ世代か project revision（`KeyCapture.Revision`）が変わると全破棄します。
  - live 再利用（同じフレームの再 Update、hover 変化など）で rects が同じキー・revision のものなら、そのまま残します（Keep）。
  - キャッシュ表示時は記憶した rects を復元します（Restore）。
  - rects が無い場合: 一時停止中かつマウスがプレビュー外なら、キャッシュから即時表示して「欠落」を記録します（Defer）。`TimelineVideoPlayer.Edit()` の prefix が、その位置で 100 ms 静止したら（マウスがプレビュー上なら直ちに）`isTimelineChanged = true` を1回だけ立て、host が通常描画して rects を作ります。それ以外（再生中、マウスがプレビュー上）は通常描画です。
  - rects の再利用は、確認済みの host（4.56.1.0 の MVID、`List<(IVideoItem, RawRectF, Vector2[], DrawDescription, IEnumerable<VideoController>)>` 型一致）だけで有効です。`Edit`/`isTimelineChanged`/`isMouseOverPreviewArea` が無い版では Defer しません。
  - メッシュ変形（`MeshDeformation`）の操作点は、編集にならない（revision が変わらない）点選択状態を snapshot するため、これを含むプロジェクトでは rects を再利用しません。ほかの組込み controller（CenterPoint、DrawPosition、Crop、Mask、Radial*、Crash、MotionTracking、Line shape）は model と frame だけに依存することを 4.56.1.0 のコードで確認しました。
- 一時停止中に通常描画したフレームの画素は保存しません（従来どおり保存は idle 先読みと出力のみ）。一時停止中のシークが速くなるのは idle 先読み済みの範囲です。
- テスト: `tests/StoreChecksHarness/PreviewRectsChecks.cs`（Linux で成功）、`tests/HostCacheProbe/PreviewRectChecks.cs`（実 host の TimelineSource と player の stand-in で Keep/Restore/編集後の破棄/ShowOnlyPreviewEffect の分離/Defer→1回の refresh/マウス上と再生中の通常描画を確認。Windows で実行）。

### 2026-09-30 追記（Claude Code クラウドセッション、branch `claude/frame-render-readiness`、draft PR #1）

環境: Linux クラウドコンテナ（GPU・YMM4 本体なし）。.NET SDK 10.0.112 / Harmony 2.4.2 / ilspycmd 11.1 を導入済みです。YMM4 DLL はこの環境に届いていません（manjubox.net は遮断されています）。

**実装したこと**
- `FrameRenderReadiness.cs`（新規）
  - `TimelineSource.Update` を AsyncLocal scope で囲みます（Prefix: Priority.First / Finalizer: Priority.Last）。decoder の Finalizer で「要求時刻 t を含む sample を保持しているか」を判定し、失敗は親 scope へ伝搬します。
  - 帰属を失った decode（EC 非 flow、完了済み scope）は処理中の全 frame を失敗扱いにします。例外は保持し、インストール途中で失敗した場合は自分が追加した patch だけを戻します。
  - host binding: host / Plugin / host dir の `YukkuriMovieMaker.Plugin.FileSource.*` にある `IVideoFileSource` 実装を**すべて**フックします（interface map、MethodHandle で照合）。分類は次のとおりです。
    - MF2: `decodedFrame` の `SampleTime`/`SampleDuration`（long 100ns または TimeSpan）が t を含むこと。
    - legacy: `currentTime`/`currentDuration`/`streamStartTime`。stream start が非 0 のときは両方の解釈で t を含む場合だけ ready です。
    - `CachedVideoFileSource`: 内部ソースの field が1つで、その実体が MF2/legacy かつ t を保持していること。
    - その他: 常に未確認扱いです。
  - 遅延読込: インストール後に組込み assembly が読み込まれた場合は、その場で分類・フックします。bind epoch（奇数＝フック中）で、変更をまたいだ frame は未確認扱いです。フックできない型がある場合は `CoverageProblem` で全停止します。
  - API: `IsUpdateReady(source)`（scope の source 一致も確認）、`WasLastUpdateReady(source, time)`（時刻一致も確認）、`Coverage`（分類一覧）、`Summary`（ツール表示）。
- `TimelineFrameCache.cs`
  - 永続キーを `pixels-v4` にし、DXGI adapter（vendor/device/subsys/rev/名称）、UMD driver version、plugin MVID を含めました。識別に失敗した場合は bypass です。
  - `Clear()` が `cacheGate` を保持したままディスク消去を待ち、描画スレッドを止めていた問題を修正しました。
  - hit 時はストアの snapshot を ReadOnlyMemory で共有します（1080p で約 8 MiB の複製を廃止）。
  - 保存を見送った理由を状態表示に出します。
- `FrameCacheStore.TryGet` を `out ReadOnlyMemory<byte>` に変更しました（snapshot は挿入後に不変）。
- `IdleFramePreRenderer`: viewport 比較から LastDrawTimestamp を除外しました（同じ view の再描画で batch が毎回中断され、device を作り直していた問題の修正）。prime には最新の viewport を使います。
- `KeyDependencyTracker.TryCapture(..., settle: true)`（描画経路のみ）を追加しました。編集後 250 ms 以内はモデルを再記述せず bypass します（連続編集で毎フレーム全体を JSON 化していた問題の修正）。親や reader の変化検出は、未無効化のときだけ行うようにしました。
- 状態・理由文字列を日本語化しました。
- `tools/HostShapeReport`（新規）: host の形状と binder の分類予測を出力します（`ShapeRules.cs` を ReadinessChecks と共有）。
- `FrameTimeKey`: フレーム境界から 1/8 フレーム（最大 1 ms）以内の時刻は、フレーム番号＋fps でキーにします（host と idle の時刻の丸め差で、先読みがヒットしない問題の対策）。
- `FrameCacheStore.PutOwned`: キャプチャ直後の配列は複製せずに保存します。
- テスト
  - `tests/ReadinessChecks`（新規、host 非依存。emit した実 DLL で遅延読込も検証）を追加しました。
  - `tests/StoreChecksHarness/TimeKeyChecks.cs`: 1〜240 fps で、整数・double・ms の丸め差、衝突、フレーム途中の時刻を検査します。
  - StoreChecks に「hit は 64 KiB 未満の確保」を追加し、Windows 専用のロック区間は他 OS で skip します。
  - HostCacheProbe: `PreviewViewport` の15引数へ追従しました。また Clear 後に一度再描画されて再利用が再開すること、`Render readiness coverage` の出力と MF2 認識の検査、組込み reader DLL の事前読込を追加しました。

**検証済み（Linux）**
- ReadinessChecks: Debug/Release で各20回連続成功、0 warnings。主要規則11件の変異テスト（wrapper の内部状態、legacy の stream start、遅延フック、epoch、遅延失敗の報告、親への伝搬、非帰属 decode、完了済み scope、時刻の束縛、例外、未確認ソース）をすべて検出しました。
- StoreChecks: Windows 専用区間以外はすべて成功しました。hit 時の複製を戻す変異も検出しました。
- Vortice 依存コード（Capture/Upload/RenderEnvironment ほか）を抜き出してコンパイル: **3.5.0 で 0 errors**。3.3.4 は SizeI がなく、3.6 以降は `CopyFromMemory` の引数が uint になるため失敗します。このことから YMM4 同梱の Vortice は 3.5.x と推定しています（要確認）。
- 注意: YMM4 参照なしのプラグイン全体ビルドは、宣言の CS0246 で止まり、メソッド本体を検査しません。**YMM4 DLL を使う Release ビルドは未確認**です。

**性能上の懸念と設計案（要判断、実測待ち）**
- 描画経路では、`KeyDependencyTracker.TryCapture` が依存ファイルの metadata lease（budget 0）を毎フレーム取り、Postfix の `Validate()` でも再検証します。ファイル1つあたり `CreateFile` 2回と祖先ディレクトリの属性取得などで、既存計測では 0.6 ms/ファイル（1ファイル時）でした。`tests/FileLeaseChecks` に 10/50/200 ファイルの計測を追加したので、Windows で実測してください。
- 依存ファイルが 256 を超えると bypass します。台詞ごとに wav がある一般的なゆっくり動画では、キャッシュが効かないか、毎フレームの負担が大きくなる可能性があります。
- キーはプロジェクト全体のモデルなので、どこか1か所を編集すると全フレームが無効になります（AE は編集した layer の時間範囲だけを無効化します）。今回、連続編集中の毎フレーム再直列化は抑えました（settle 250 ms）。
- 設計案: フレーム f のキーを「global（VideoInfo/LayerSettings/設定/reader/characters）＋ f に重なるアイテム（layer 順）の状態ハッシュ＋それらのファイル指紋」にします。状態ハッシュはアイテム単位でキャッシュし、変更通知の sender 単位で無効化します。これで編集範囲外のフレームは再利用でき、lease も重なるアイテムのファイルだけで済みます。
  - SceneItem は参照 timeline 全体を含めます。
  - 他アイテムの描画結果や音声を参照する可能性がある型（音声波形、画面の複製など）は、ILSpy で確認して whitelist 外なら全体キーに戻します。
  - host の item 型の知識が必要なので、DLL 到着後に実装する想定です。

**YMM4 4.56.1.0 の実バイナリでの確認結果（2026-10-01）**

ユーザーが公式 Lite zip を private repo `sabiasagimp4-ai/ymm4-dlls` の Release に置き、クラウド側で ILSpy と MetadataLoadContext で読みました（DLL や逆コンパイル結果は commit していません）。**zip の中身は 4.56.1.0 で、PC の 4.55.1.1 とは別版**です。

- **ビルド**: 4.56.1.0 の DLL を参照して、プラグイン（Release）、HostCacheProbe、CacheChecks が 0 warnings / 0 errors で通りました。ManagedSmoke は C# 部分のみ成功で、ネイティブ DLL は Windows ビルドが必要です。HostCacheProbe の `IdleFramePreRendererChecks` に既存のコンパイルエラー（internal な `KeyCapture.Key` の参照）があったので修正しました。
- (a) **MF2**（`MFVideoFileSource2`）: `decodedFrame.SampleTime/SampleDuration` は TimeSpan です。Update 自身が `decodedFrame != null && SampleTime <= t < SampleTime + SampleDuration` を有効判定にし、失敗時は null で透明を描きます。binder の判定はこれと同じです。
- (b) **旧 MF**（`MFVideoFileSource`）: Update の冒頭で `time += streamStartTime` とし、`[currentTime, +currentDuration)` を stream 時計で保持します。timeout・エラー・範囲外では `currentDuration = 0` です。判定は `Covers(t + streamStartTime)` に確定しました。
  - **FFmpeg**（`FFmpegVideoFileSource`）も同じ時計を使います。ただし EOF や**読込エラー**でデコードが途中で止まると、直前のフレームを stream 終端まで引き延ばして表示するため、終端まで届く区間は未確認として扱います（最終フレーム付近は保存しません）。
- (c) `VideoFileSourceFactory.Create` は、すべての動画ソースを `CachedVideoFileSource(filePath, VideoResource(devices, source))` で包みます。wrapper の Update は常に `resource.Source.Update(time)` に委譲します。旧規則（直接の field）ではここが未確認になり、**動画を含むフレームがまったく保存されない状態でした**。判定を `resource.Source` に修正済みです。
  - WIC の GIF/WebP は同期デコードで、失敗は例外になります（GIF が握りつぶす1種のエラーは、そのファイルでは毎回同じ結果です）。このため「例外なし＝完了」として扱います。
  - 連番画像（読込失敗が黙って空になる）と DirectShow は未確認のままです。
- (d) 実行時の確認が必要です（遅延読込は bind で対応済み）。
- (e) `TimelineSource.Update` は Parallel.ForEach / AsParallel / Task.Run を使い、EC は flow します。ただし**先読み（`PrefetchResources`）は約1秒先のアイテムを Task.Run で作って Update するため、フレーム終了後にデコードすることがあります**。採用するフレームが自分の scope で再度 Update するので、完了済み scope 上のデコードは無視するよう変更しました。
- (f) `CreateFileAsync` は `start = max(0, min(min(from,to), len-1))`、`end = min(len, max(from,to))` で、1フレームにつき `WriteVideo` を1回呼びます。`HostExportScope.ExpectedFrames` と一致します。writer は最初の await より前に作られます。
- (g) `TimelineVideoPlayer` は `Update` を `Draw()`（BeginDraw）の**前**に呼びます。Postfix でのキャプチャは描画と衝突しません。
  - 再生中は `NeedTimelineItemRects = isMouseOverPreviewArea`、一時停止中は常に true で usage は `Paused` です。このため当時は**マウスがプレビュー上にある再生中と一時停止中のシークがキャッシュ対象外**でした（上の「追記（2）」で対応）。
- (h) wrapper の Update は小さいですが、内側のデコーダーの Update は大きく、内側でも同じ状態検査をするので安全側です。
- (i) host 自身が `TimelineSource.Update` の最後に毎回 `CacheProvider.Clear()` を呼びます。`Hit()` の Clear はこれと同じなので問題ありません。
- (j) `YukkuriMovieMaker.ItemEditor.TimelineSourceAndDevices` は player と同じく `new GraphicsDevices()` を使います。内部 field `source` も想定どおりです。
- フレーム時刻: player は `VideoInfo.GetTimeFrom`（`round(frame×10⁷/fps)`）を使います。idle は切り捨てで 1 tick ずれていたので、同じ変換に揃えました。
- 版: `HostIntegration` は 4.55.1.1 と 4.56.1.0 の MVID/SHA-256 を受け入れます。4.56.1.0 の値はこの zip から算出しました。4.55.1.1 の decoder 形状は未確認ですが、binder は型名と形状の両方を照合するので、合わなければ未確認扱いになります。

**次に Windows で確認すること**
1. `tools/HostShapeReport` を手元の YMM4 に対して実行し、4.55.1.1 でも `predicted:` が MF2 / MF-legacy / FFmpeg / WIC / wrapper になるか確認します。または YMM4 を 4.56.1.0 に更新します（未保存プロジェクトを保存してから）。
2. `build.ps1 -Smoke`（GPU を使う HostCacheProbe と NativeSmoke を含む）を実行します。
3. HostCacheProbe の `CheckVideoDecodeFailureIsNotStored`（追加済み、未実行）を確認します。`build.ps1 -Smoke` は ManagedSmoke が作る `dist/managed-audio-first.mp4` を `--video` で渡します。
   - 中身: 実 MediaFoundation reader で VideoItem をデコードして再利用を確認します。次に MF2 は `MFFrameDecoder.TryDecodeAt` を false に、旧 MF は timeout 時と同じ `ClearCurrentFrame` にして、そのフレームが保存も再利用もされないことを確認し、復旧後に再利用が戻ることを確かめます。

### 以前の状態（2026-10-01 checkpoint 時点）

- 最新ソースの Release ビルドは失敗しています。引継ぎ作成時にも再確認しました。
- `TimelineFrameCache.cs` の129・255・325行で CS0103: `FrameRenderReadiness` が未定義。0 warnings / 3 errors。
- 呼出し側だけ接続され、`FrameRenderReadiness.cs` は未実装です。常に成功するstubでビルドを通してはいけません。
- プレビュー／idleの最後の変更は統合試験未実行です。既存 `dist` は最新ソースに対応しません。インストールや配布に使わないでください。
- **YMM本体の起動・画面操作による検証をしない**という最新のユーザー指定を守ってください。ビルド、host DLLを使うoffline試験、GPU harnessは許容範囲です。
- 今回は資料と関連WIPコードをGitHubへ保存する依頼です。開発完了とは区別してください。

## ユーザーの要求・許可

1. YMM4でAEに近い自動キャッシュを実現する。対象は **編集プレビューと動画出力の両方**。
2. 停止・不具合を可能な限り除去し、実測で軽量化する。全バグゼロや全停止回避を無根拠に保証しない。
3. 対応版限定の実行時フックは許可済み。本体ファイルを変更せず、未知版は無効化する。
4. GitHubへのcommit/pushは依頼済み:
   https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC
5. サブエージェント調査を希望。指定は GPT-6 Luna、最大推論。利用できない場合は代替を黙って同一扱いしない。担当ファイルを分け、他者の差分を戻さない。
6. Qiita参考:
   https://qiita.com/harupython/items/4be768e58cba3a2921b3
7. **YMM GUI検証なしで進める**。元YMMには未保存プロジェクトがあるため、操作・終了・再起動・インストールをしない。

## 環境・Git・作業規約

- workspace: `D:\Windowsフォルダ移動\ダウンロード\YMM_keiryouka`
- host: `D:\YukkuriMovieMaker_v4_Lite`、YMM4 Lite 4.55.1.1。
- Windows / PowerShell / .NET 10 / Visual Studio 2022 C++ / RTX3060 12 GiB。
- branch: `main`、originは上記ユーザーrepo。upstreamへpushしない。
- checkpoint直前のpush済み基準commit:
  `c0bb1ea8c485a29c11abfbc25304c8592bcded1c`
  — `fix: bound NVENC queues and drain GPU inputs before cleanup`
- この資料を含むcheckpoint commitのIDは `git log -1` で確認する（自己参照hashを埋め込まない）。
- **`tools/AssemblyDump/Program.cs` は開始前からのユーザー差分。checkpointには含めずローカルに保持**。GitHubだけから引き継ぐ場合、この差分はありません。
- `bin/obj/dist/vendor`、host DLL、逆コンパイルソース、調査用GUIコピーはcommit対象外。
- 編集はapply_patch。破壊的reset/checkout・広域削除禁止。素材や既存完成出力を保持する。
- AGENTS.md: codebase-memory MCPでコード発見を優先。project `D-Windows-YMM_keiryouka` は再index後も17 nodes / 16 edges、関数検索が不十分だったため直接ソースへfallback済み。利用可能なら再確認する。
- ILSpy: `tools/bin/ilspy/ilspycmd.exe`。必要なhost型のみ解析し、host実装を製品へコピーしない。
- 調査用コピー `tests/bin/GuiHost-7789f4ba9c264073aebe7cf41138932e` はignored。削除はpolicy拒否済みで残っています。別経路で拒否を回避しない。
- サブエージェントへ保存・停止を通知済み。旧agentが引き続き実行中／再利用可能という前提を置かない。

## 実装マップと試験境界

以下の成功は直前作業で得た結果であり、**checkpoint全体の最新統合成功ではありません**。最新ビルド失敗を優先してください。

### Native NVENC — 基準commitでpush済み

- slot別GPU texture所有、完了までmapping維持。
- output queueは64 MiB / 256 samples、30秒無進捗failure、writer failureで待ち解除。
- AAC内部PCMは1024 frames、NaN/Inf対応。
- HEVC NAL、MP4 timescale・64bit duration・fps丸めを修正。
- Finalize/Destroyは共通EOS flush→pending lock/unlock/unmap。失敗時session先行destroy。
- NativeChecks、GPU H264/HEVC出力とdecode、各codecのcancel/failure試験は成功済み。
- ドライバー自身のハング・実device-lossは未検証。in-flight driver APIを強制中断できる保証はない。

### Managed writer — WIP、個別試験成功済み

担当: `NvencVideoFileWriter.cs`、`HostExportScope.cs`、`HostIntegration.cs`、plugin入口、ManagedSmoke。

- IVideoFileWriter3 GPU対応。先行音声をDeleteOnClose spoolし64 KiBずつreplay。
- native呼出しを専用MTA threadに集約、bounded 1 queue、例外伝搬、Dispose競合/join。
- constructorでhost取消tokenと予定frame数をsnapshot。取消・不足時に既存完成ファイルを置換しない。
- 取消後audio/video呼出しにもThrowIfCancellationRequested。
- GPU／並行audio-video／2 Dispose／取消／不足／publish／異寸法のManagedSmoke成功。
- 32 MiB先行音声の全thread合計managed allocation: 12,896 bytes。
- driver呼出しが戻らない場合Dispose joinも待つ限界がある。
- HostExportScopeはfactory前hookで接続。実hostのscope不明／未知版では出力開始拒否。
- host/Plugin/Settingsの配置・MVID・SHA256を確認。Harmony 2.4.2、MIT全文をTHIRD_PARTYへ追加。
- HostCacheProbeに追加したVerifyHost positive assertionは最新状態で未実行。

### FrameCacheStore — 独立harness成功済み

担当: `FrameCacheStore.cs`、`tests/StoreChecksHarness`、`tests/CacheChecks/StoreChecks.cs`。

- RAM 256 MiB / disk 4 GiB / frame 128 MiB、LRUと件数制限、raw lossless、SHA header、atomic rename。
- constructor/Put/TryGetでdisk I/Oをしない。owner lock、epoch Flush(true)、index/read/write/hashは専用worker。
- disk-only初回TryGetは即miss、dedup background readでRAMをwarmにし次回hit。
- queue 128 operations / queued writes 128 MiB、満杯ならdrop。
- Clearはserialized durable generation barrier。旧read/write取消、旧epochのlocked fileは再起動で復活せずphysical bytesに計上。
- owner不在／初期化失敗時はRAM invalidate後IOException。成功を偽らない。
- worker異常時はactive/queued Clear TCSをfaultし無期限待ちを防止。
- disk使用にはRAM > 0必要。RAM budgetを超えるframeはclone前にrejectしdiskにも保存しない。
- 独立StoreChecksHarness成功。CacheChecks側の重複実行は除外済み。

### ファイル依存とキー — owned tests成功済み

担当: `FileDependencyLease.cs`、`FrameCacheKey.cs`、`KeyDependencyTracker.cs`、CacheChecks/FileLeaseChecks。

- FileShare.Read lease、NTFS volume/FileID/length/mtime/ChangeTime + SHA。UNC/ancestor reparseはbypass、256 files cap。
- 新規hashだけbudget課金。exact stamp一致ならwarm leaseはbudget 0でも可能。
- shared fingerprint index 512 entries FIFO、open lease + exact stamp検証後に再利用。
- 64 MiB cold約34 ms、warm約0.6 ms（限定測定）。
- tracker hashはglobal Semaphore 1、background新規hash上限512 MiB、fault/backoff/CTS cleanup。
- KeyCaptureはKey/Revision/Model/file leaseを保持、Validate/Dispose競合guard。
- Invalidated eventでidle取消、subscriber exceptionを分離。
- built-in reader type/MVIDをキーへ。mutable runtime reader配列は軽いtype-sequence検査。
- file-backed sceneにcustom readerがloadedならbypass。未知external effects/params、Tachie transient lip-syncもbypass。
- CacheChecks/FileLeaseChecks成功後、Shape.X 0.0004→0.00049→restoreの精度regressionを追加。**この最後の追加は未実行**。

### プレビュー — 途中、最後の変更は未検証

担当: `TimelineFrameCache.cs`、HostCacheProbeのProgram/FramePixelChecks。

- TimelineSource.UpdateのcommandList fieldを差替えcollectorへ所有移管。normal Update/Dispose releaseを実証。
- Exporting自動RAM/disk reuse、background/ShapeItem alpha/negative bounds/odd width 321のBGRA parityは成功済み。
- 単純raw scene replayはlate zoom/panで画素不一致が判明したため不可。
- 現設計: TimelineVideoPlayer.Draw前にactual viewportをpublishし、opaque-blackのsource-onlyをviewport transformでcapture。inverse(transform + center offset) commandListへreplay。
- shadow/controller/scrollbarsはhostのlive描画に残す。キーへviewport signature、playing/last draw timestamp、Scene/Timeline ID。
- idle cloneのTryPrimePreviewのみでpixel recordsを保存。live missはvector出力のまま。
- 前版HostCacheProbeはactual Update/Dispose hooks、GPU ownership、export hits/parity/invalidation/cleanup、late viewport transform parity成功。
- micro測定0.21 ms/update baseline vs 0.01 ms/update reuseは3/8 samplesの限定値。速度保証ではない。
- 最新変更: readiness helper呼出し、actual backbuffer PixelFormat/alpha、AA/TextAA/PrimitiveBlend/UnitModeをviewportへ追加、keyはpixels-v3。capture/uploadでmodesを保存・復元。
- **FramePixelChecks.csのPreviewViewport constructorは新signatureへ未更新**。helper実装後にtest compileも修正する。
- 追加すべきproof: actual primed Playing hit / A→B→A、NeedRects(selection/controller) bypass、viewport変更miss、Clear世代/GPU release、TextItem/font/opaque vs premultiplied target parity。
- live visited-frame保存の軽量経路は未実装。idle prime限定をAE完全再現と扱わない。
- persistent keyへのGPU adapter/driver identity反映も未実装。

### IdleFramePreRenderer — 保存済み、最終試験未実行

担当: `IdleFramePreRenderer.cs`、`FrameCacheTool.cs`、IdleFramePreRendererChecks。

- 独立cloned Scenes/Timelines/Characters、Voice/Tachie CharacterName rebinding。
- KeyCapture.Modelからdeserializeしclone/live key + modelを照合。
- 独立TimelineSourceAndDevices workerでPlaying update後、viewport/live key確認してprime。
- input/edit/seek/playback/busyでcancel。NextFrame cursor、約1秒horizon、MaximumFrames 30。同範囲終了後の反復回避。
- enabled preferenceをplugin ctorでも適用。toolを開く前から有効化。
- settings Save failureをcatch/reportしsession choice維持。ITimelineToolViewModel→TimelineToolInfo injection。status日本語化。
- using System.IO/private Timeline.Length setterのcompile問題は修正し、一度Release成功。
- clone independence/model parity/簡易cancel成功後、capture pair + Harmony counter、取消後TryPrime未到達の試験へ強化。**強化版は未実行**。
- full timeline進行、繰返しidle cursor、model serializeのUI待ち時間は要review。

## 最優先: FrameRenderReadiness実装

必要API（現在の呼出し側と照合する）:

```csharp
TryInstall(host, cacheHarmony, out reason)
IsReady
WasLastUpdateReady(hostTimelineSource)
```

目的はdecoder timeout/errorの透明出力を完成frameとして永久保存しないこと。public Updateが正常returnしただけではreadyの証明になりません。

静的解析で確認済み:

- CacheProviderは完成frameではなくunused IDisposable resourcesのpool。TryGet hitはリストからremove、Clearは残存idle resourcesをDispose。
- TimelineSource.UpdateはParallel.ForEach終了を待つ。
- MF ReadSampleAsync後、SourceReaderSession.ReadSampleはCompletion.Wait(timeout)。prefetchも採用時GetResultでjoin。
- よって「async callbackなのでUpdateが未完了のままreturn」はこのhostの問題ではない。
- **ただしMF Updateはtimeout/errorでも透明出力で正常returnする。**
- MF2の有効条件: decodedFrame != null && SampleTime <= t && t < SampleTime + SampleDuration。
- legacyはtimeout/errorでcurrentDuration = 0 / ClearCurrentFrame。
- GetFrameIndexもreadyを保証しない。image bitmap作成は同期。audioはこのCacheProvider対象外。

最小設計案（実host形状を確認してから実装）:

1. 同じcacheHarmony ownerでTimelineSource.Updateへvoid Prefix + Finalizer。
2. PrefixでAsyncLocal scopeを設けParallel.ForEachへflowし、decode failureをInterlocked latch。
3. FinalizerでCWTへlast-resultを保存、親scopeへ復帰、例外時failed。
4. MF2 Update Postfixでprivate decodedFrameのsample intervalを照合。
5. legacy Update PostfixでcurrentTime/currentDuration/streamStartTimeを照合。
6. CachedVideoFileSource.Updateのunderlyingがstrict MF2/legacy以外ならfail closed（shape-onlyは許容）。
7. idleのTimelineSourceAndDevicesはprivate sourceをunwrapしてlookup。
8. exact field/method shape/Harmony ownerを確認。途中failure時はhelper自身のpatchだけrollback。

## 次に行う順序

1. Git状態とこの資料の根拠を確認。ユーザー差分・元YMMを保持。
2. FrameRenderReadinessを実装しdecoder失敗の回帰試験を追加。成功stub禁止。
3. FramePixelChecksのsignature修正、viewport/modes/text parity、actual Playing hit、bypass/invalidation/GPU解放を検証。
4. idle clone/cancel/horizon/cursorを検証。UI負荷を測る。live visited-frame再利用の残要件を整理・実装。
5. owned tests → 製品Release → HostCacheProbe → full Smokeの順で再検証。YMM GUIは起動しない。
6. Smoke成功後のみ最新package/hash生成。旧distを最新成果と混同しない。
7. READMEのWIP注意は完成条件を満たすまで残す。結果と未検証境界を報告しcommit/push。

## コマンド

```powershell
git status --short
git log -3 --oneline

dotnet build NVEncVideoWriterPlugin/NVEncVideoWriterPlugin.csproj -c Release '-p:YMM4DirPath=D:\YukkuriMovieMaker_v4_Lite\' --nologo

dotnet run --project tests/StoreChecksHarness/StoreChecks.csproj
dotnet run --project tests/FileLeaseChecks/FileLeaseChecks.csproj
dotnet run --project tests/CacheChecks/CacheChecks.csproj

# helperとtest compileを直した後。各csproj/READMEの引数も確認:
dotnet run --project tests/HostCacheProbe/HostCacheProbe.csproj -c Release -- 'D:\YukkuriMovieMaker_v4_Lite' --gpu
.\build.ps1 -Smoke
```

build.ps1はManaged/Store/FileLease/Cache/HostProbe/NativeChecks/NativeSmoke cancel/failure/ffmpeg checksを統合する変更済み。package生成はWrite-PluginPackageへ移しSmoke後に実行、partialからFile.Move(overwrite: true)して旧package先行deleteを回避。PowerShell parser確認済みだが **最終full Smoke未実行**。

## 参考記事と採用境界

QiitaのHarmony/Update省略は参考にした。記事repoは調査時404、F3はhostと衝突、POHはGCゼロを意味しない。libvips(LGPL)は未導入、AMFはAMD専用なのでNVENCを維持。ライセンスを確認してから依存を追加する。

NVENC入力自体はCPU readbackなし。ただしlossless cacheのpixel captureにはCPU readbackがあり、両者を混同して「全経路readbackゼロ」と宣言しない。

## 完成判定の注意

引継ぎ完了と製品完成は別です。preview/export両方の自動再利用、編集・素材更新・取消・Clear・異常decodeの整合性、上限とGPU寿命、回帰parity、測定を揃えてください。GUI検証を行わない制約下で確認できないhost実使用は未確認として明記し、全バグ・全停止・AE完全互換を保証しないでください。
