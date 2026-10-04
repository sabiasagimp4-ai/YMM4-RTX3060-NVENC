# ホスト契約と監査の維持

基準は公式YMM4 Lite 4.56.1.0の実バイナリを読んだ契約。旧引継ぎの実装上の注意を現行コードに合わせて集約した。利用者向けの挙動は [CACHE_BEHAVIOR.md](CACHE_BEHAVIOR.md)。

## 対応版の判断

`HostIntegration` の既知ビルドは配置・MVID・SHA-256を確認する。4.56.1.0が全機能の基準で、4.55.1.1は一部機能のみ。NVENC出力は `HostExportScope` のメソッド／field構造が一致する版で接続し、出力フックの失敗はキャッシュ全体を停止しない。

未知版のキャッシュは `HostContracts`／`HostFingerprint` が前提コードを機能ごとに照合する。`core`、`preview`、`selection-rects`、`wrapped-sources`、`ruler-bars`、`decoder:<assembly>` に依存関係があり、不一致の機能を通常描画へ落とす。照合成功は記録した前提との一致であり、新版全体の実使用保証ではない。

fingerprintは型・基底・interface・field・属性・正規化IL・生成型を対象とする。witnessはusage、他シーン、item picker、素材列挙、描画設定、controller、動画ソース生成等の前提を参照するコードも収集する。対象の変更を小さく見せるためにwitnessを削らない。

結果は `%LOCALAPPDATA%\YMM4-RTX3060-NVENC\host-contracts.json` に、本体DLL群とプラグインのMVIDをキーとして保存する。

## デコード完成判定

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
