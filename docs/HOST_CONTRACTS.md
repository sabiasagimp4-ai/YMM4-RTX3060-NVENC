# ホスト契約と監査の維持

基準は公式YMM4 Lite 4.56.1.0の実バイナリを読んだ契約。旧引継ぎの実装上の注意を現行コードに合わせて集約した。利用者向けの挙動は [CACHE_BEHAVIOR.md](CACHE_BEHAVIOR.md)。

## 対応版の判断

`HostIntegration` の既知ビルドは配置・MVID・SHA-256を確認する。4.56.1.0が全機能の基準で、4.55.1.1は一部機能のみ。NVENC出力は `HostExportScope` のメソッド／field構造が一致する版で接続し、出力フックの失敗はキャッシュ全体を停止しない。

未知版のキャッシュは `HostContracts`／`HostFingerprint` が前提コードを機能ごとに照合する。`core`、`preview`、`selection-rects`、`wrapped-sources`、`ruler-bars`、`decoder:<assembly>` に依存関係があり、不一致の機能を通常描画へ落とす。照合成功は記録した前提との一致であり、新版全体の実使用保証ではない。

fingerprintは型・基底・interface・field・属性・正規化IL・生成型を対象とする。witnessはusage、他シーン、item picker、素材列挙、描画設定、controller、動画ソース生成等の前提を参照するコードも収集する。対象の変更を小さく見せるためにwitnessを削らない。

結果は `%LOCALAPPDATA%\YMM4-RTX3060-NVENC\host-contracts.json` に、本体DLL群とプラグインのMVIDをキーとして保存する。

## デコード完成判定

シンプル立ち絵の `simple-tachie` はcoreとwrapped-sourcesに依存し、同梱SimpleTachie全体、TachieSource、Character、TachieItem、TachieFaceItem、IFaceItemを追加で照合する。ホストのpickerをそのまま呼んで可視表情を選ぶ。4.56.1.0のSimpleTachieは音量の-1だけを非表示に使い、characterのDirectoryは編集UIでしか読まない。型・同梱配置・MVID `be62ee72-e935-4cca-9bba-eb9de4a27cde` も確認する。既定・ボイス・上の表情の画像は実際に選ばれる区間に入れ、使われないfaceのファイルはそのボイス／faceアイテム自身の描画依存から外す。共通の字幕・音声エフェクトが同じファイルを使う場合はそちらの依存を維持する。番号付き画像とグループの時間対応は未確認のため対象外にする。新しいHarmonyフックは追加していないが、記録済み4.56.1.0の全基準をHostFingerprintで再生成している。

`TimelineSource.Update` をAsyncLocal scopeで囲み、要求時刻と実際の動画ソースの状態を検査する。例外なしのreturnだけで完成とは判定しない。

| ソース | 4.56.1.0で読んだ条件 |
| --- | --- |
| MF2 | decodedFrameが存在し、SampleTime ≤ t < SampleTime＋SampleDuration |
| 旧MF | streamStartTimeを足した要求時刻がcurrentTime／currentDurationの区間に入る。timeout等ではdurationが0 |
| FFmpeg | 同じstream時計。読込エラーでも直前画素を終端へ引き延ばすため、終端まで届く区間は保守的に未確認 |
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

他アイテム／シーン・音声・未報告ファイル・時計・乱数・native／通信・可変static・前フレーム画像を読む処理を調べた。MotionBlur／AfterImage／CircularBlurの履歴依存（CircularBlurはコードを読んだ後の試し描きで判明、[EXTERNAL_PLUGINS_2026-10-04.md](EXTERNAL_PLUGINS_2026-10-04.md)）、AudioVolume、OpenFx、未報告ファイルや未監査処理は自動対象にしない。CameraShake等の同一性をseedとする処理はSessionキーとなり、idle複製は避ける。

ShuffleText／ShuffleTextInOutは、フレーム番号（と入力の番号）だけを種にしたMersenneTwisterで文字を選ぶ。NumberTextは値を `double.ToString`（現在のカルチャ）で書式化する。3つとも `Font` の名前をYMM4と同じくフォント設定から引き（なければArial）、DirectWriteで描く。キーには `Font` から解決したフェイスとファミリーのファイル、インストール済みフォントの識別、NumberTextではカルチャの数値書式を入れる。4.56.1.0のYMM4はUIのカルチャ（`CurrentUICulture`）だけを設定し、`CurrentCulture` はOSの設定のまま。

ユーザーが信頼したアセンブリも名前とMVIDをキーに含め、設定変更でtrackerを再記述する。この信頼は隠れた依存を検出する仕組みではない。CommunityのMVIDが変わった場合は再監査してからリストを更新する。

## ホスト更新の手順

`ymm4-watch` はmainで毎日06:17 JSTに更新サーバーを確認する。新しい版はmanifestのサイズ・ハッシュを照合して取得し、contracts・Windowsビルド・有効な機能のprobeを実行してissueへ結果を残す。実行時間はActionsのスケジュール遅延に影響される。

1. issueと型／method差分を読み、公式取得したDLLを調査する。逆コンパイルした実装は製品へコピーしない。
2. 前提が変わった場合は規則・キー・witnessを修正する。一致していない版をKnownHostsへ追加するだけで有効にしない。
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

共有設定の JSON は通知の世代と全 scalar/list の bounded な witness が一致するときだけ再利用する。通知なしの子も毎回確認し、エンコードは捕えた値から行う（再読込の A/B/A で witness と JSON を取り違えない）。module の不変の監査結果は AssemblyLoad の世代で再確認し、collectible の依存 DLL を高速経路に入れない。変更・大きな素材の数値は [Claude レビュー対応（2026-10-05）](SPEEDUP_REVIEW_RESPONSE_2026-10-05.md)。
