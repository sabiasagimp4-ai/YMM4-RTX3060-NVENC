# ホスト契約と監査の維持

基準は公式YMM4 Lite 4.56.1.0の実バイナリを読んだ契約。旧引継ぎの実装上の注意を現行コードに合わせて集約した。利用者向けの挙動は [CACHE_BEHAVIOR.md](CACHE_BEHAVIOR.md)。

## 対応版の判断

プラグインは参照する `YukkuriMovieMaker.dll` の版（YMM4の版と同じ）より古いYMM4では読み込まれない。YMM4は「必要なファイルを読み込めませんでした」（`YukkuriMovieMaker, Version=<参照した版>`）と表示する（4.56.1.0でビルドしたものを4.47.0.0・4.50.0.0・4.56.0.1で起動して確認、2026-10-05）。

そのため、プラグインはコードを読んだ4.56.1.0でビルドし、ビルドの後に参照の版を対応する最も古い版（`Ymm4MinimumVersion`、4.47.0.0）へ下げる（`NVEncVideoWriterPlugin/HostReferenceVersion.targets`、`tools/HostReference`）。4.47.0.0は.NET 10で動く最初の版で、4.46以前は.NET 8・9なので.NET 10のプラグインを読み込めない。

その版にないメンバーは `HostApi` を通してだけ使う。存在を確かめてから、そのメンバーだけを名指す別のメソッドで呼ぶ（名指すメソッドはJITのときに失敗するため）。YMM4はプラグインの全部の型を読み込むので、その版にないinterfaceを実装する型は置かない。`IVideoFileWriter3`（4.54）は `GpuWriterProxy`（DispatchProxy）が実行時に実装し、それより前の版にはGPUフレームを受ける `IVideoFileWriter2` として書き込みクラスを渡す。CI（cache-development）は、最も古い版に対してプラグインの全型が読み込めること（HostLoadChecks）と、足りないメンバーが `tools/compat/guarded-host-apis.txt` に挙げたものだけであること（`HostFingerprint api`）を確かめる。新しいメンバーを使うときは `HostApi` に加え、この一覧へ足す。

`HostIntegration` の既知ビルドは配置・MVID・SHA-256を確認する。4.56.1.0が全機能の基準。NVENC出力は `HostExportScope` のメソッド／field構造が一致する版で接続し、出力フックの失敗はキャッシュ全体を停止しない。

4.56.1.0以外のキャッシュは `HostContracts`／`HostFingerprint` が前提コードを機能ごとに照合する。`core`、`preview`、`selection-rects`、`wrapped-sources`、`ruler-bars`、`identity-random`、`decoder:<assembly>` に依存関係があり、不一致の機能を通常描画へ落とす。照合の相手は、コードを読んだ版（`HostBaselines.cs`）と、読んだ版との違いを読んだ版（`HostReviewedBuilds.cs`、次の節）。coreが一致する記録の機能を合わせて使う（coreが同じなら、各機能は自分の部品と前提の機能だけで決まる）。照合成功は記録した前提との一致であり、新版全体の実使用保証ではない。

fingerprintは型・基底・interface・field・属性・正規化IL・生成型を対象とする。witnessはusage、他シーン、item picker、素材列挙、描画設定、controller、動画ソース生成等の前提を参照するコードも収集する。対象の変更を小さく見せるためにwitnessを削らない。

結果は `%LOCALAPPDATA%\YMM4-RTX3060-NVENC\host-contracts.json` に、本体DLL群とプラグインのMVIDをキーとして保存する。

## 古い版（4.47.0.0〜4.56.0.1）

更新サーバーにある4.47.0.0〜4.56.0.1の54版は、4.56.1.0との違いを読んで、機能ごとに確かめた（2026-10-05）。版ごとに、契約の部品、危険なAPI（乱数・時計・ファイル・設定・他シーン・usage等）を使う型、古い版にだけある描画の型のうち4.56.1.0と異なるものを並べ、隣り合う版の逆コンパイルの差分を読んだ。確かめた機能の部品のdigestを `HostReviewedBuilds.cs` に記録し、同じdigestが続く版は1つの記録にまとめる。記録した版でもコードが記録と1か所でも違えば、その機能は使わない。フレームのキーは本体のMVIDを含むので、版の違うYMM4のフレームを使い回すことはない。

| 版 | 使う機能 |
| --- | --- |
| 4.55.0.0〜4.56.0.1 | core、preview、selection-rects、wrapped-sources、ruler-bars、identity-random、MF・WIC・FFmpegの完成判定 |
| 4.47.0.0〜4.54.0.1 | 上からselection-rectsを除く（FFmpegの動画読み込みは4.52.0.0から。4.52.0.1以前のidentity-randomはプラグインの種のそろえ込みが前提） |

読んだ違いと、その扱い:

- 乱数（`identity-random`、`RandomSeedAlignment`）: 4.52.0.1以前のランダム系エフェクト（`RandomEffectBase` のランダム移動・回転・拡大・不透明度・傾き）は、描画器が作るエフェクトの処理オブジェクト自身を種にする（`GetHashCode()`、`GetRandomMoveRate(this, …)`）。同じシーンでも描画器ごとに別の値になる（実ホストで、2つ目の描画器は30フレームすべてが違った）。4.52.0.2からはエフェクト（`item`）が種。プラグインは4.52.0.1以前でも、処理オブジェクトの `GetRandomValue` の呼び出しを、4.52.0.2と同じくエフェクトを種にする計算（ホストの `GetRandomValue` が呼ぶメンバーで組み立てる）に置き換える。`GetRandomValue` そのものは書き換えない。全ての `T` が共有するコードで、.NETが最適化して作り直すとHarmonyの書き換えが外れるため（手元の実験で確認）、呼び出し側の5つの `Update` を書き換える。置き換えるのは、呼ぶメンバーの並びが読んだ形（4.47.0.0〜4.52.0.1で同じ）のときだけ。テキスト・字幕のランダム順の表示は、全版で描画器（`TextSource`／`JimakuSource`）自身を種にする（`GetHashCode()` + 文字数）。描画器はアイテムが画面に入り直すたびに作り直されるので、YMM4だけでは入り直すたびに順が変わる。プラグインはこの `GetHashCode()` の呼び出しを、描くアイテム（`TextItem`／`VoiceItem`）の同一性のハッシュに置き換え、キーにはそのアイテムを入れる（4.50以前は `TextSource` の1か所、4.51からは両方の2か所ずつ）。どちらも値の乱雑さは変わらない（同一性のハッシュは任意の値）が、同じプロジェクトのプレビュー・出力・先読みが同じ値を描く。置き換えられないとき（形が違う、Harmonyの失敗）は、その乱数を持つ区間を通常描画する。照合の部品は `RandomEffectBase` と、描画の名前空間で乱数を作る・同一性のハッシュを取るすべての型。4.47.0.0〜4.56.0.1の乱数の種は、この2つ以外は4.52.0.2と同じ。クラッシュ・ランダム複製・ノイズはアイテムかパラメーター、ドロネー・ボロノイのモザイクはパラメーター（4.56.1.0も同じ。キーに入れたのは2026-10-06から）、ランダム移動のアニメーションはAnimationが種。`VideoItem`／`VoiceItem` のハッシュは分割時のグループ番号だけ。`Animation.GetRandomMoveRate` は4.52から値のキャッシュを持つが、値は同じ。
- プレビュー: 4.54.0.1以前のプレイヤーにはズーム・パンがなく（`PreviewDisplayZoom`、`PreviewViewCenter`、`GetVisibleVideoSize`、`CreatePreviewViewTransform` がない）、Drawはcontextの変換のまま出力を (幅/2, 高さ/2) に描く。`TimelineFrameCache` はこの4つがすべてない版では、viewの変換をcontextの変換だけにする（一部だけある版は契約違反として使わない）。
- 選択枠: 4.54以前は `TimelineItemRects` の要素の型が違い、型の確認で自動的に使わない。
- FFmpeg: 4.54.0.0以前には、シークした位置が要求時刻より後だったときに戻ってシークし直す処理（`SeekAndDecode`）がない。そのときは後のフレームが要求時刻からの区間として残り、どのフレームになるかがシークの履歴で変わる。そこで完成判定は、シークしたUpdateが要求時刻ちょうどから始まる区間を作ったとき、その区間を別のフレームがデコードされるまで未完成とする（`SeekTo` をフックしてシークを知る。下のデコード完成判定の表）。4.56.1.0でも、ストリームの先頭からでも届かないときは同じ区間が残るため、全版で同じ扱いにした。順にデコードした区間はどの版でも同じフレームになる。4.52は `streamStartTime` がなく、要求時刻をそのまま使う（ずらしは0）。4.52.0.0〜4.54.0.0のタイミングの処理は4種類で（4.52.0.0〜4.52.0.3は終端の引き延ばしなし、4.52.0.4〜4.52.0.8は `streamStartTime` なし、4.53.0.0〜4.53.0.4と4.53.0.5〜4.54.0.0は区間の長さの計算だけが違う）、どれも区間の始まりは「フレームの開始と要求時刻の早いほう」、失敗・終端・範囲外では長さ0か終端まで引き延ばし（既存の判定で除外）。画像の書き込みの途中で戻る条件は4.56.1.0と同じ。4.51以前には動画の読み込みがない。実ホストで、シークを繰り返す順に4回描いた120フレームのうちキャッシュから出たのは、MP4が4.52.0.0・4.54.0.0で60、4.56.1.0で87、MPEG-TS（.m2ts）が4.52.0.0で21、4.56.1.0で63で、すべて先頭から順に描いた絵と一致した（4.52.0.0の.m2tsではYMM4自身がシーク後に違う絵を32フレーム描き、保存しなかった）。4.54.0.0の.m2tsは保存0。YMM4が長さを短く読み（4秒の動画を2.53秒）、1枚を0.2〜0.53秒の区間として持つので、シークした区間が要求時刻から始まったまま次のシークまで変わらない（シーク後の多くのフレームでYMM4自身が違う絵を描いた）。
- 旧MF: 4.53.0.0から `streamStartTime` がある（4.52以前は形の確認で未確認）。4.54.0.1以前は同期の読み込みでtimeoutと作り直しがなく、エラー・終端でdurationを0にするのは同じ。4.54.0.0以前は断片化MP4の長さの解析がなく、4.53.0.1以前は長さから `streamStartTime` を引かない（範囲が長くなるだけで、終端の先は読めずdurationが0）。4.54.0.1ではHarmonyが `MFVideoFileSource.Update` を作り直せず、旧MFは未確認になる（wrapperが拒否する）。
- MF2: 4.48から（4.47にはない）。完成判定の条件は4.48から同じ。
- WIC: 4.51以前は連番の読み込みプラグインの名前の翻訳だけが違う。
- 番号付き画像: `VideoSource.CalculateSourceTime` は4.56.0.0から。それより前は、ルートのアイテムの連番はキーにしない（通常描画）。
- 文字の制御タグ: 4.50以前にはない（アイテム自身の装飾だけで描く）。4.51の `ControlTagParser.Parse` は字間の引数がなく、`HostApi` がreflectionで呼ぶ。
- 停止中の先読み: 複製に使う `Scenes(bool)` は4.49から。4.48以前は先読みしない。`VideoInfo.BackgroundColor`（4.52から）は、ある版だけ複製へ写す。
- フレーム時刻の丸めは4.52.0.8以前で違うが、キーも描画もその版の `VideoInfo` の変換を使うので一致する。
- 立ち絵: 同梱の立ち絵のMVIDが4.56.1.0と違うため、どの古い版でも使わない（4.55.0.1以前は `LipSyncEnvelopeSession` もない）。起動時の報告も、同梱の立ち絵が読んだものでなければ立ち絵を使わない機能に数える。

記録を作り直すときは、確かめた版をすべて渡す（`HostContracts.Rules` を変えたときも同じ）。

```powershell
dotnet run --project tools/HostFingerprint -- reviewed NVEncVideoWriterPlugin/HostReviewedBuilds.cs '4.56.0.1=D:\YMM4\4.56.0.1@core,preview,...' ...
```

## デコード完成判定

シンプル立ち絵の `simple-tachie` はcoreとwrapped-sourcesに依存し、同梱SimpleTachie全体、TachieSource、Character、TachieItem、TachieFaceItem、IFaceItemを追加で照合する。ホストのpickerをそのまま呼んで可視表情を選ぶ。4.56.1.0のSimpleTachieは音量の-1だけを非表示に使い、characterのDirectoryは編集UIでしか読まない。型・同梱配置・MVID `be62ee72-e935-4cca-9bba-eb9de4a27cde` も確認する。既定・ボイス・上の表情の画像は実際に選ばれる区間に入れ、使われないfaceのファイルはそのボイス／faceアイテム自身の描画依存から外す。共通の字幕・音声エフェクトが同じファイルを使う場合はそちらの依存を維持する。番号付き画像とグループの時間対応は未確認のため対象外にする。新しいHarmonyフックは追加していないが、記録済み4.56.1.0の全基準をHostFingerprintで再生成している。

`TimelineSource.Update` をAsyncLocal scopeで囲み、要求時刻と実際の動画ソースの状態を検査する。例外なしのreturnだけで完成とは判定しない。

| ソース | 4.56.1.0で読んだ条件 |
| --- | --- |
| MF2 | decodedFrameが存在し、SampleTime ≤ t < SampleTime＋SampleDuration |
| 旧MF | streamStartTimeを足した要求時刻がcurrentTime／currentDurationの区間に入る。timeout等ではdurationが0 |
| FFmpeg | 同じstream時計（4.52は開始時刻のずらしなし）。読込エラーでも直前画素を終端へ引き延ばすため、終端まで届く区間は保守的に未確認。シークしたUpdateが要求時刻ちょうどから始まる区間を作ったら、区間が変わるまで未確認（シークが要求時刻より後に着いたときの後のフレームと区別できないため。シーク1回につき最大1フレーム） |
| CachedVideoFileSource | resource.Sourceへ委譲し、検証済み内側ソースの状態を確認 |
| WIC GIF／WebP | 同期decodeの例外を確認。読み取った契約の範囲で扱う |
| WIC連番 | `GetFrameIndex(t)`（60枚／秒）の画像を同期で読み込み済み（`source` あり、`currentFrame` 一致）。読めない画像は空の画像を描くので未完成とする。加えて、キーが示す画像を実際に表示したことを確認する |
| DirectShow等の未確認ソース | 通常描画し、保存しない |

連番の各フレームがどの画像を表示するかは、ホストの `VideoSource.CalculateSourceTime`（internal。再生速度とそのアニメーション、開始位置、長さ、ループ、ソースの長さ）と `FrameTime.TimeToFrame(t, 60)` をそのまま呼んで求める（`ImageSequence`）。ファイル一覧は読込側と同じ規則（拡張子の種類が動画でない、名前の末尾が数字、同じ接頭辞・拡張子のファイルを番号順に並べ、指定したファイルの番号から連続する範囲。最小の番号でないファイルを指定すると一覧は空で、連番にならない）で作る。読込側は一覧を一度だけ作り、ホストはそのソースをパスごとに使い回すため、起動中に一覧が変わった連番はキーにしない。TimelineSourceは1秒先までのアイテムのソースを同じscopeで先読みするので、「表示した画像がすべてキーにある」ではなく「キーが示す画像がすべて表示された」を条件にする。

Harmony 2.4.2は一部の例外フィルター付きmethodを作り直せない。DirectShow等のpatch失敗を成功扱いせず、検証できるwrapperによって未確認の内側を拒否する。wrapper等の必須契約を確保できない場合は対応機能を無効にする。

後読み込みのbind epochをまたいだフレームを採用しない。ホストのresource prefetchが完了済みscopeを引き継いで後からdecodeする場合は、そのscopeへの記録を無視する。採用するフレーム自身のUpdateで再確認する。帰属不能のdecodeを成功と仮定しない。

## 描画・資源の契約

- playerはTimelineSource.Updateの後にDraw／BeginDrawする。同じD2D contextをworker間で並行使用しない。
- `CacheProvider` は完成画素キャッシュではなく、未使用IDisposable resourceのpool。ホストもUpdate末尾でClearする。
- 単純なscene command listの再表示はlate zoom／panで画素が一致しなかった。現在は実viewport・黒背景・描画modeを含めてcapture／復元する。
- closed command listでも可変effect graphへの参照は不変画像にならない。GPU保持するのはUploadPreviewが生成した書換えないbitmapのcommand listだけ。
- 保存するmissフレームは一度だけ描く。保存用にviewへ描いたbitmapをUploadPreviewと同じ1:1 command listで出力へ差し替え、playerのDrawは合成を再評価せずblitする。ホストの出力はホストのdisposerに残し、Draw直前のviewが描いたviewと異なれば（ズーム・パン・サイズ変更）それへ戻す。このcopyはGPU保持しない。
- 保存の読み戻しはソースごとに3枚まで同時に待つ（全部がGPU上にある時だけ保存を見送る）。1回のUpdateで仕上げるのは原則1枚。描画先とstagingはソースごとのpoolで使い回し、表示中のcopyの描画先は出力から外れてからpoolへ戻す。
- cacheとsourceのCOM参照を分離し、LRU退避後も表示中borrowを壊さない。世代・key・context・依存を採用直前にも照合する。
- Harmony finalizerは例外がなくても走る。遅延保存へ渡すcaptureは `Pending.HandOver()` で移管し、`DeferredStore.Dispose` がReleaseする。
- 停止時の遅延保存は対応playerのBeforeEditで仕上げる。refresh契約がない版は同期readbackが必要となる場合があり、GPU非待機を一律保証しない。
- idle複製はTimeline.Lengthも写し、liveの検証済み指紋を引き継ぐ。キーとlease指紋の一致を省略しない。複製・tracker・`TimelineSourceAndDevices` は1本のworker threadで作成・使用・破棄し、モデルと検証済み指紋が同じ間はバッチをまたいで使い回す。仕事が2秒途切れたら解放する。
- ボイスの圧縮配列は4.56.1.0のホストでは読み取り専用で、生成時は配列を差し替える。idleの複製には記述の内容と一致した配列だけを共有する。JSONに入らない音声パスは `VoiceItem.customVoiceFilePath` に設定し、ライブの `TemporaryFile` の所有権は移さない。元のパスの変更はcaptureの採用時にも確認し、通知がなくても記述し直す。このfieldと `FilePath` は、既存のcoreのGetFiles witnessが収集するVoiceItem全体のfingerprintに含まれる。fieldを確認できない版では、その複製を拒否する。
- フレーム時刻はホスト同様 `VideoInfo.GetTimeFrom` で作り、正確なticksをキーにする。丸めて別sampleを共有しない。
- 素材をホストが保持したまま上書きすると、新しい指紋で古い画像を保存し得る。`HostContent` の再起動までのbypassを、ファイルwatch通知だけで解除しない。

## Communityと外部コードの監査

`KnownCode.VerifiedCommunity` は4.56.1.0のCommunityの固定MVID `ac765de8-d44f-44f1-a094-961becf4d22e` と読込場所を確認する。型の正確な一覧と対象外理由は [KnownCode.cs](../NVEncVideoWriterPlugin/KnownCode.cs)。

他アイテム／シーン・音声・未報告ファイル・時計・乱数・native／通信・可変static・前フレーム画像を読む処理を調べた。MotionBlur／AfterImage／CircularBlurの履歴依存（CircularBlurはコードを読んだ後の試し描きで判明、[EXTERNAL_PLUGINS_2026-10-04.md](history/EXTERNAL_PLUGINS_2026-10-04.md)）、AudioVolume、OpenFx、未報告ファイルや未監査処理は自動対象にしない。CameraShake等の同一性をseedとする処理はSessionキーとなり、idle複製は避ける。

ShuffleText／ShuffleTextInOutは、フレーム番号（と入力の番号）だけを種にしたMersenneTwisterで文字を選ぶ。NumberTextは値を `double.ToString`（現在のカルチャ）で書式化する。3つとも `Font` の名前をYMM4と同じくフォント設定から引き（なければArial）、DirectWriteで描く。キーには `Font` から解決したフェイスとファミリーのファイル、インストール済みフォントの識別、NumberTextではカルチャの数値書式を入れる。4.56.1.0のYMM4はUIのカルチャ（`CurrentUICulture`）だけを設定し、`CurrentCulture` はOSの設定のまま。

ユーザーが信頼したアセンブリも名前とMVIDをキーに含め、設定変更でtrackerを再記述する。この信頼は隠れた依存を検出する仕組みではない。CommunityのMVIDが変わった場合は再監査してからリストを更新する。

## ホスト更新の手順

`ymm4-compat` はmainで毎日06:17 JSTに更新サーバーを確認する。まだこのプラグインの版で確かめていないYMM4の版（新しい版、またはプラグインのリリース後は全版）を、manifestのサイズ・ハッシュを照合して取得し、ファイルの照合（.NETの版・参照するDLLの版・contracts）、Windowsでの起動（プラグイン自身が使う機能を報告）、新しい版では、読んだ版でビルドしたプラグインと検査（キー検査・probe）をその版で実行する。結果はREADMEの「YMM4 の版ごとの対応」と [YMM4_VERSIONS.md](YMM4_VERSIONS.md) へ自動でcommitし、新しく公開された版にはissueを作る。古い版をまとめて確かめるときは、手動実行で `versions` に `all` を指定する。実行時間はActionsのスケジュール遅延に影響される。

1. issueと型／method差分を読み、公式取得したDLLを調査する。逆コンパイルした実装は製品へコピーしない。
2. 前提が変わった場合は規則・キー・witnessを修正する。一致していない版をHostKnownBuildsへ追加するだけで有効にしない。
3. 記録を更新し、実ホスト・decoder失敗・画素一致・無効化・資源返却を検証する。

```powershell
dotnet run --project tools/HostFingerprint -- contracts 'D:\YMM4-new'
dotnet run --project tools/HostFingerprint -- compare 'D:\YMM4-old' 'D:\YMM4-new'
dotnet run --project tools/HostFingerprint -- members 'D:\YMM4-old' 'D:\YMM4-new' 'YukkuriMovieMaker.Player.Video.TimelineSource'
dotnet run --project tools/HostFingerprint -- emit NVEncVideoWriterPlugin/HostBaselines.cs '4.56.1.0=D:\YMM4-old' 'NEW_VERSION=D:\YMM4-new'
```

emitでは渡さなかった既存版を保持するが、HostContracts.Rulesを変えた場合は全記録済み版を渡し直す。上記NEW_VERSIONは実際の版番号へ置き換える。検査の実行方法は [HostCacheProbe README](../tests/HostCacheProbe/README.md)。

## AnimationTachieと口パクの完成判定（2b）

`lip-sync-readiness` はCoreの `TachieSource` 全体、音量計算・公開session・取消slot・待機timeout latch、音声source、CharacterとVoiceItemを照合する。新しいHarmony対象は `TachieSource.Update`（呼び出し元の立ち絵を識別）と `ReadVolumeAfterRequiredWait`（消費した値の完成確認）。公開済みsampleとのbit一致、現在のsession／task、取消、終了したsessionの全sample公開を確認する。ホストは失敗を吸収するためTaskの正常終了だけでは許可しない。未完成は既存のreadiness scopeから親のsceneまで失敗を伝える。

`animation-tachie` はその規則に依存し、AnimationTachie DLLの全型とTachieItem／FaceItem／IFaceItemを照合する。追加規則の基準値は、記録済みの全版（現在4.56.1.0のみ）について `tools/HostFingerprint emit` で再生成した。実行時にもAnimationTachieのMVIDと同梱場所を確認する。未知の版／外部の型は通常描画。

PNGのみ・付属INIなしを対象とし、全候補部品と番号付き／母音部品を依存にする。一覧の変化は同期確認して再起動まで対象外にする。INI削除後もホストのLayerConfigが残るため、キャッシュの参照と保存の両方で実ソースの13layerの設定と目／口の既存parts countを確認する。既定まばたきはパスの起動ごとのhashを使うので、この段階ではprocess nonceとitem同一性を含むSessionキーを採用する。計算式の複製と起動間の共有は未対応。

停止中の先読みは、AnimationTachie の表示区間だけ追加の音量計算を開始せず見送る。表示区間外は通常の複製経路で先読みし、モデル比較で session 値だけを伏せる。各フレームのキーは完全一致が必要。動画部品、付属INI、差分合成、group、同一layerの表情競合、入れ子sceneは今回の対象外。再生／一時停止／出力でホストが完成させたフレームを再利用する範囲で検査する。

AnimationTachie の一覧は参照可能な部品名の `stem*` を同期で列挙し、一度の SafeSource 内で同じパスの結果を共有する。参照前と採用直前の検証は残す。フォルダー時刻だけで省略せず、時刻を戻した新しい部品も検出する。初期一覧の内容は16MiB、4,096件に制限し、上限超過はbypassする。候補に大文字小文字だけが異なるファイル名が同時にある場合も、依存の別名を取り違えないため対象外。

## PSD立ち絵の共有設定と保持状態（2c）

`psd-tachie` は `lip-sync-readiness` に依存し、同梱Tachie.Psd、FileSource.Psd、PsdParserの全型、TachieItem／FaceItem／IFaceItemを照合する。口パクのHarmony対象は2bの2メソッドを共有し、新しいhookは追加しない。全記録済み版（現在4.56.1.0のみ）の基準をemitで再生成した。実行時にも3つの読込moduleのMVIDと同梱配置を確認し、契約キャッシュの識別にPsdParser.dllも含める。

PSDファイルをlease／指紋／HostContentの依存にする。`PsdFileSettings.LoadFromPsdFilePath` が返す共有オブジェクトの実JSONをキーのresourceに含め、PropertyChangedを購読する。通知されない子要素の変更も、記述時と同じJSONの弱いmodel witnessを参照前・採用前に照合し、古いcaptureを無効にする。キーとwitnessは同じ設定snapshotを使う。設定検査は4,096node、深さ5、1list 1,024要素、文字合計65,536・各4,096、JSON262,144文字に制限し、未知の型や非有限数は対象外。

さらに、実ソースのPSD／root／共有設定の同一性と、正規化済み設定を共有設定のreadonly `ResolveAgainst(root)` の結果と比較する。子要素を直接書き換えたときにホストが古いnormalized設定を保持する場合は、通常描画に戻し保存しない。CPU合成失敗時の空bitmapも、PSDのcanvas寸法との不一致から拒否する。未読込・非表示でrootを解放したソースも保守的に通常描画。正規化確認のroot参照はweakであり、非表示後のPSD画像データを保持しない。通知付きlist置換でホストが正規化を更新した後に再利用できる。

sidecarの再読込を追加しない。ホストは起動中の同一パスで共有設定を保持するため、sidecarの外部上書きだけでは通常の新規sourceでも設定は変わらない。キーはsidecarの生bytesではなく、ホストが実際に使う共有設定に従う。既定まばたきにはSessionキーを用い、PSD 立ち絵の表示区間の停止中先読みは追加の音量計算を始めず見送る。表示区間外は先読みする。group、表情の同一layer競合、入れ子scene、外部立ち絵は対象外。

PSDをキャッシュ有効化前に読み込んでいた場合も、parserが保持するreadonly bytesのSHA-256（parsed file ごとに一度、背景 task・buffer コピーなし）と capture の lease 指紋を比較する。完了前・失敗時は通常描画で保存・再利用しない。不一致ならHostContentを再起動まで対象外にし、古い画素を新しいファイルのキーへ保存しない。元のstreamや配列の所有権は変更しない。

共有設定の JSON は通知の世代と全 scalar/list の bounded な witness が一致するときだけ再利用する。通知なしの子も毎回確認し、エンコードは捕えた値から行う（再読込の A/B/A で witness と JSON を取り違えない）。module の不変の監査結果は AssemblyLoad の世代で再確認し、collectible の依存 DLL を高速経路に入れない。変更・大きな素材の数値は [Claude レビュー対応（2026-10-05）](history/SPEEDUP_REVIEW_RESPONSE_2026-10-05.md)。
