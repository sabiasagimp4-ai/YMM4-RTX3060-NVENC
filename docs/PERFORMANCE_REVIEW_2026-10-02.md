# 性能調査レポート（main `23e4fdb` 時点）

2026-10-02。対象は `main` の現行コード（`23e4fdb`）。製品コードは変更していない。

## 0. 根拠の種類と読み方

| 種類 | 内容 | 注意 |
| --- | --- | --- |
| コード | 現行 `main` の実装。`ファイル:行` で示す | 行番号は `23e4fdb` |
| CIトレース | `docs/traces/**/*.jsonl.gz` を再集計（集計スクリプトはこの調査で作成） | GitHub Windows runner、**WARP（ソフトウェアD3D）・論理CPU 2**。GPU処理がCPU時間に入る。RTX 3060の値ではない |
| RTX 3060実測 | `docs/preview-performance-rtx3060.json`（`e60c455`、実 `TimelineSource.Update` + 代替Draw、100図形+Arial文字、1080p、disk無効） | 古いcommitだが `FileDependencyLease.cs` は現行と差分なし。readbackは当時同期 |
| 推定 | コードから費用構造が確定していて、大きさだけ未測定のもの | 「要計測」と明記 |

YMM4本体のバイナリはこの環境から取得できなかった（更新サーバーへの接続がproxyで拒否）。ホスト側の挙動は、リポジトリ内のILSpy調査記録（`docs/HOST_CONTRACTS.md` とコードコメント）に依拠している。

---

## 1. Executive Summary

**最も重要な性能問題：キャッシュ有効時の「素材ファイル依存の検証」が、1回の `TimelineSource.Update` の中で最大8回、描画スレッド上で繰り返される。**
`FileDependencyLease.TryAcquire` 1回と `StillCurrent` 7回が、そのフレームが使う全素材ファイルについて、パス解決・祖先ディレクトリ全段の属性取得・ドライブ種別取得・`CreateFile`・`GetFileInformationByHandleEx`×3 を毎回行う。CIトレースではmiss 1フレームあたり検証7回（p50 7.7 ms、p95 26 ms）、GPU hitでは処理時間の約90%が検証とキー生成だった。リポジトリ自身のRTX 3060実測では、キャッシュOFFのUpdate 0.157 msに対し、ON（cold）10.2 ms、RAM hit 5.25 ms、そのうちキー生成だけで2.1 ms（素材はArialのフォントファイルのみ）。素材ファイル数に比例して増え、シーンアイテムや音声波形図形を含むフレームではプロジェクトの全素材が対象になる。

**最大のボトルネック候補（キャッシュON時）**
1. 上記の依存検証（CPU・syscall・ウイルス対策のファイルフィルター。描画スレッドを直列に占有し、全体ロック `cacheGate` も保持する）。
2. 保存されるmissフレームで合成グラフを**2回評価**していること。`BeginPreviewReadback` が host の出力 command list を別のtargetへ描き直し、playerの `Draw` がもう一度描く。加えて毎フレーム、viewportサイズのD2D target・D3D11 stagingの生成と、8 MB級のLOH配列確保（ゼロ埋め）・memcpyを描画スレッドで行う。
3. 停止中の先読み（idle）の効率。30フレームごとにプロジェクトJSONの再読込・複製・全体記述・新しい `TimelineSourceAndDevices`（新しいデコーダー）を作り直す。複製ソースの `Update` がライブ用キャッシュフックを素通りできず、無駄なキー生成と検証を行う。readbackは同期。バッチ間に約50〜500 msの空白がある。CIでは、30フレームのバッチで本体 `Update` の合計は時間の15〜25%だった。

**導入しただけ（機能OFF）のオーバーヘッド：ある。ただし小さい。**
フックは設定に関係なく起動時に入る。対象は `TimelineSource.Update`（2系統）・`Dispose`、`TimelineVideoPlayer.Draw`・`Edit`、全 `IVideoFileSource.Update`（ラッパーと内側の両方）。毎フレーム、数個の小さな確保とreflection、`ConditionalWeakTable` 操作がある。RTX 3060実測のOFF時 `TotalUpdate` p50 0.157 ms がフック込みの上限で、体感できる遅延の原因である可能性は低い（YMM4単体とのA/Bは未測定）。

**実測なしで改善できること**
- 検証を「最終採用（ストアへのcommit・出力の差替え）直前の1回」に減らす。途中のチェックは世代・リビジョン・環境など安価なものだけにする。
- idle複製ソースをライブ用キャッシュフックの対象外にする。
- 画素配列を `GC.AllocateUninitializedArray` で確保する。
- ディスク書込のスレッド優先度を下げる。
- GPU hit時にも読み先行（read-ahead）を実行する。
- idleバッチを250 msのタイマーを待たずに続ける。
- メモリ監視で `Process.PrivateMemorySize64`（全プロセスのスナップショット）を毎秒取得しない。

**実測が必要なこと**
- 二重描画除去のGPU上の効果（D3D11 timestamp query）。
- ディスク層のハッシュ・圧縮方式。
- 再生中のwrite-throughがデコードと競合する度合い。
- LOHとGCの影響。
- 機能OFF時のフック費用（YMM4単体とのA/B）。
- キャッシュhitが本体 `Update` を飛ばすことによるデコーダー不連続（仮説。既存ログでは確認できず）。
- 再生開始までの時間（TTFF）とseek latency。

**よくできている設計**
- ライブプレビューの非待機readback（`Map(DoNotWait)`）。
- ディスクI/Oの専用worker化（読込優先・上限付きキュー・原子的書込）。
- GPU保持の頻度ベースadmission。
- 編集中のsettleと大規模プロジェクトの背景記述。
- NVENC出力のGPUテクスチャ直渡し（async depth 4・所有テクスチャへのコピー・上限付きキュー）。
- 常時ONの計測（`PreviewPerformance`）が十分軽い。

**1つだけ改善するなら：依存検証を「1 Updateにつき最大1回（最終commit時）」に減らす**（§8.1）。キャッシュONの全フレーム（miss・RAM hit・GPU hit・idle）に効き、キャッシュ内容と採用条件は変わらず、変更範囲も小さい。これを先に除かないと、他の改善の計測がこのノイズに埋もれる。

---

## 2. 前提（ユーザーの懸念）の検証

| 懸念 | 判断 | 根拠 |
| --- | --- | --- |
| プラグインで1フレームのレンダー時間が伸びている | **キャッシュONなら正しい。原因はレンダリングではなく検証とreadback**。機能OFFならほぼ当たらない | RTX 3060: OFF 0.157 ms → ON cold 10.2 ms / RAM hit 5.25 ms（§4 F1・F3）。CI: ON時は本体 `Update` 自体もp50で約1.3〜1.6倍（WARP・2コアでの競合。§4 F6） |
| AEのように重いフレームで待つ方が良い | **再生中の `Update` で待つのは逆効果**。YMM4の再生は音声時計（`TimelineAudioPlayer.Position`）で進み、映像の待機は同期ずれとフレーム落ちを増やす。「再生前にキャッシュしてから再生する」方式（Cache Before Playback）として実装すべき | `docs/AE_CACHE_DEVELOPMENT.md` の時計の記述、§6 C4 |
| 停止中に別フレームを計算してキャッシュできる余地 | **機能は既に実装済み**（`IdleFramePreRenderer`）。課題は量ではなく効率 | §4 F4・F5。CIでは本体 `Update` がバッチ時間の15〜25% |
| フレーム落ち対策をさらに改善できる | 正しい。ただし主因はプラグイン自身の費用。hit経路は大半が検証。missは本体描画が支配的で、プラグインはそこに負荷を上乗せしている | §4 F1・F2・F6 |
| （追加）キャッシュは速くなるはず | 軽いプロジェクトでは**逆に遅い**（RAM hit 5.25 ms 対 本体描画 0.16 ms）。立ち絵を含むフレームは常に対象外（§4 F12） | 採否判定（`FrameCacheEconomics`）は保存を抑えるだけで、キー生成と検証は毎フレーム走る |
| （追加）CPU・GPU使用率が低ければ余裕がある | 当てはまらない。検証は描画スレッド1本を直列に占有するため、8コアなら全体CPU使用率は約12%にしか見えない。その間GPUは次の提出を待つ | §4 F1 |

---

## 3. フレーム要求から表示・書出しまでの実経路

描画スレッドはplayerの描画タスク（UIスレッドではない）。playerは `TimelineSource.Update(time, usage)` の後に `Draw` する（`docs/HOST_CONTRACTS.md`）。

### 3.1 常時（機能OFFでも）— 描画スレッド、毎フレーム

1. `FrameRenderReadiness.UpdatePrefix`（Priority.First）：`object[] __args`、`new Scope`、`ConcurrentDictionary` への追加、`AsyncLocal` の設定（`FrameRenderReadiness.cs:223-231`）。
2. `TimelineFrameCache.Prefix`：`new UpdateMeasurement`、`ConditionalWeakTable` の Remove+Add（`TimelineFrameCache.cs:364-374`）。`CachePrefix` は `CompleteDeferred` の確認後、`!Enabled` で戻る（`:376-381`）。
3. 本体 `TimelineSource.Update`。動画アイテムごとに `IVideoFileSource.Update`。ラッパー `CachedVideoFileSource` と内側（FFmpeg/MF）の両方に prefix と finalizer があり、`object[] __args` とreflectionによる `HoldsFrame` が走る（`FrameRenderReadiness.cs:247-281`）。
4. Postfix と Finalizer（統計のリングバッファへのlock）。`UpdateFinalizer` の `lastResults.AddOrUpdate` と `AsyncLocal` の復元（`:233-245`）。
5. player `Edit` → `BeforeEdit`（CWT照会のみ）。player `Draw` → `ObservePlayer`：reflectionで viewport を計算（`MethodInfo.Invoke`×2、`PropertyInfo.GetValue`×4〜5、boxing）し、`WeakReference` を3個確保、`cacheGate` をlockする（`TimelineFrameCache.cs:933-968`）。続いて本体 `Draw` と `DrawFinalizer`（統計）。
6. UIスレッド：キャッシュバーが250 ms周期（Background優先度）。プレビューキャッシュOFFなら即return。

### 3.2 プレビューキャッシュON・miss（再生中）

描画スレッド、順番どおり：

1. **前フレームの回収** `CompleteDeferred`（`:584-626`）：`Map(Read, DoNotWait)`。完了していれば `new byte[W*H*4+32]`（LOH、ゼロ埋め）→ 行ごとの `Marshal.Copy` → `Unmap`。`StillCurrent` を2回（**各回が全素材ファイルを再検証**）、`Economics.ShouldAdmit`、`store.PutOwned`（RAMのLRUへ挿入し、ディスク書込をキューへ）。
2. **キー生成** `CachePrefix`（`:383-427`）：reflection（`needRects`・scene・picker field走査）、`ValidContext`（COM getter）、`KeyEnvironment` 文字列、`Tracker.TryCapture`（`:404`）→ tracker lock・`SourceReadersMatch`・`FrameDependencyIndex.For` → **`FileDependencyLease.TryAcquire`**（`KeyDependencyTracker.cs:173`）。ファイルごとに `GetFullPath`、`IsLocalPlainPath`（`DriveInfo`＋祖先全段の `GetFileAttributes`）、`FileStream` open、`GetVolumeInformationByHandleW`、`TryStamp`×2（各3 syscall）、最後に `VerifyPaths`（**同じファイルをもう一度open**）（`FileDependencyLease.cs:41-80`）。
3. viewportをreflectionで再計算する。`MakeKey`×2（文字列化＋SHA-256＋hex）、`GpuAdmission.Observe`、`TryRestoreGpu`（miss）、`store.TryGet`（lock、`ToLowerInvariant`）。**ReadAhead**：再生中は0.5〜2秒先（最大120フレーム）のキーを毎回すべて `ComposeKey`（SHA-256）し、`store.Prefetch`（lock）する（`:637-660`）。
4. 本体 `Update`（デコーダーフック込み）。
5. `CachePostfix`（`:506-548`）：`IsUpdateReady`、`StillCurrent`×2（`:520`、`:532`）。`StorePreview`（`:553-580`）→ `BeginPreviewReadback`（`:1185-1240`）：viewportサイズの **D2D targetを新規作成**（`:1207`）→ 文脈状態を退避 → `BeginDraw`・`Clear` → **`DrawImage(output)`：合成グラフの1回目の評価**（`:1217`）→ `EndDraw`（提出）→ surface→texture→device → **stagingを新規作成**（`:1233`）→ `CopyResource` → `Flush`。最後に `StillCurrent`（`:573`）。
6. player `Draw`：本体が同じ `output` を backbuffer へ描く（**2回目の評価**）。overlayを描き、Present。
7. ディスクworker（別スレッド・通常優先度）：Brotli（quality 0）→ SHA-256 → 一時ファイルへ書込 → rename。必要ならLRUで古いファイルを削除。

RTX 3060（`e60c455`、軽い図形プロジェクト）：`TotalUpdate` p50 **10.19 ms**（OFF 0.157 ms）。内訳はKeyGeneration 2.13、MapWait 1.73（当時は同期）、CpuMemcpy 1.83、HostRender 0.38、BeginGpuCopy 0.29（CPU側のみ）。残り約3.8 msは計測区間外で、`StillCurrent` の検証と推定。

### 3.3 GPU hit

`CompleteDeferred` → キー生成（`TryAcquire` 込み）→ `TryRestoreGpu`：`cacheGate` 内で `StillCurrent`（`:1400`）→ `QueryInterface` → `CommitReplacement`（再び `StillCurrent`、`:1421`）で出力を差し替える → `Hit()`。**本体 `Update` は実行されない**。ReadAheadも実行されない（`:458-464` がReadAhead `:476` より前でreturnするため）。player `Draw` は保持したbitmapのcommand listを描くだけ。

CI（WARP）：`timeline-update` p50 1.76 ms。うちKeyGeneration 0.79 ms＋検証2回 約0.8 ms。差替え（borrow+commit）自体は0.013 ms。

### 3.4 RAM hit

3.3でGPUがmissした後、`store.TryGet` が当たる → `TryReplaceFrame`（`StillCurrent`）→ `UploadPreview`：D2D bitmapを新規作成 → `CopyFromMemory`（8 MB、CPU→GPU）→ command listを記録（逆viewport変換、NearestNeighbor）→ `CommitReplacement`（`StillCurrent`）→ `RetainUploaded`（頻度判定でGPU保持へ）→ ReadAhead → `Hit()`。RTX 3060：`TotalUpdate` p50 5.25 ms（KeyGeneration 2.16、CacheLookup 2.90）。

### 3.5 ディスクのみのフレーム

再生中は待たずにmissとし、読込をキューへ入れて本体で描画する（次回からRAM hit）。停止中は最大50 msまで読込を待つ。ディスクworkerの処理は open → header → 確保 → read → Brotli展開 → SHA-256照合 → `AddRam` → pulse。再生中にディスクから供給されるのは、ReadAheadが間に合ったフレームだけ。

### 3.6 停止中の先読み（idle）

UIスレッドの `DispatcherTimer`（250 ms、Background優先度）が条件を満たすと、`LongRunning` タスクで1バッチ（最大30フレーム）を起動する（`IdleFramePreRenderer.cs:128, 214`）。

- **バッチごと**：liveのtrackerで `TryCapture`＋`Validate` → **プロジェクトJSON全体を `Json.LoadFromText`**（`:249`）→ `CloneScene` → **`new KeyDependencyTracker(clone)`**（最初のcaptureで全体記述をinline実行）（`:252`）→ **`new TimelineSourceAndDevices(clone)`**（新しいデコーダー群）（`:253`）。
- **フレームごと**：`CanContinue` → `TryGetLatestPreviewViewport`（`cacheGate`）→ `TryCapturePair`（live・cloneで `TryCapture` 2回、`Validate` 2回）（`:267`）→ `Validate`×2（`:281`）→ `IsPreviewStored`（`MakeKey`＋residency）→ **`source.Update`**（`:292`）。このUpdateにもライブ用 `CachePrefix` が走り、複製ソース用の**3つ目のtracker**（バッチ初回は全体記述）、キー生成、`StillCurrent`×2 を行う → `TryPrimeIfCurrent`（`Validate`×2）→ `TryPrimeCore`：`TryCapture`（再度）→ `MakeKey` → **`CapturePreview` は同期**（target生成・再描画・staging生成・`CopyResource`・`Map`（待機あり）・確保・memcpy）（`TimelineFrameCache.cs:868`）→ `Validate` → `PutOwned`（ディスク書込も）→ `Thread.Yield`。

CI（`gui-admission` / `gui-lru` / `gui-trace`）：30フレームのバッチが346〜838 ms、そのうち本体 `Update` の合計は67〜180 ms。バッチ間の空白は44〜500 ms（概ね200 ms前後）。idleの `Update` 自体もp50 2.23 msのうち、本体は0.37 ms、ライブ用フックのKeyGeneration 0.78 ms、検証0.86 ms。

### 3.7 動画出力（NVENC）

本体の書出しスレッドでの順序：`Update`（出力キャッシュOFFなら `EnabledFor` で即return）→ `WriteVideo(ID2D1Bitmap1)` → `EncoderThread.Invoke`（同期でスレッドを切り替える）→ ネイティブで、所有する入力テクスチャのスロットへ `CopyResource` → `nvEncMapInputResource` → `nvEncEncodePicture`（async、depth 4。N−4フレームの完了イベントを待つ）→ 戻る。MP4への多重化は専用writerスレッド。CPU readbackはない。

出力キャッシュON：missでは `CaptureScene` → `Capture`。target・CPU-read bitmapを生成し、**2回目の `DrawImage`** → `EndDraw` → `CopyFromBitmap` → **`Map(Read)` はGPU完了まで待つ** → LOH確保とmemcpy → `PutOwned`（ディスク書込）（`TimelineFrameCache.cs:1111-1163`）。hitでは `Upload`（CPU→GPU）。

---

## 4. Findings

### F1 依存ファイル検証の多重実行（最重要）

**【Finding】**
キャッシュ有効時、1回の `Update` で素材ファイル依存の検証が最大8回走る（`TryAcquire` 1回＋`StillCurrent` 7回）。1回の検証でフレームが使う全ファイルを再オープンする。7回の大半（トレース上の典型的な順序では6回）は全体ロック `cacheGate` を保持したまま行う。

**【Evidence】**
- `TimelineFrameCache.StillCurrent`（`TimelineFrameCache.cs:686-697`）→ `KeyCapture.Validate`（`KeyDependencyTracker.cs:550-551`）→ `FileDependencyLease.VerifyPaths`（`FileDependencyLease.cs:119-134`）。
- `VerifyPaths` 1ファイルあたりの処理：`IsLocalPlainPath`（`new DriveInfo(root).DriveType` と祖先全段の `File.GetAttributes`、`:136-144`）→ `File.OpenHandle`（CreateFile）→ `TryStamp`（`GetFileInformationByHandleEx`×3）→ Close。
- `TryAcquire` 1ファイルあたりの処理：上記に加え `FileStream` open、`GetVolumeInformationByHandleW`、`TryStamp`×2、最後に `VerifyPaths` で同じファイルを再度open（`:41-80`）。約25 syscall／ファイル。
- 呼出し箇所：`:520`、`:532`、`:573`、`:599`、`:604`／`:612`（次フレームのprefixで実行される `CompleteDeferred`）、hit経路の `:1400`、`:1421`、`:1442`。
- CI（`2026-10-02-nonblocking/ram-64`）：render経路で `capture-dependency-validation` が **1 Updateあたり7回、合計p50 7.7 ms／p95 26 ms**（単発では最大65 ms）。GPU hit経路は2回。
- CI（`2026-10-01-gpu/gpu-hit`、軽いfixture）：GPU hit 1.76 msのうち、KeyGeneration 0.79 msと検証2回で約1.6 ms。
- RTX 3060（`e60c455`、`FileDependencyLease.cs` は現行と同一）：KeyGeneration p50 **2.13 ms**。素材はArialファミリーのフォントファイルのみで、キャッシュOFFの `TotalUpdate` は0.157 ms。

**【Execution frequency】**
キャッシュ対象の全 `Update`（miss・RAM hit・GPU hit・live）×フレームの依存ファイル数。idleではフレームごとにさらに多い（F4・F5）。

**【Why it matters】**
費用はファイル数×検証回数に比例する。上の実測から1ファイル1回あたり約0.1〜0.4 msとすると、フレームが20ファイルに依存すれば、miss 1フレームで推定10〜50 ms（要計測）。シーンアイテムや音声波形図形があるフレームはプロジェクト全ファイルに依存する（F11）。描画スレッドを直列に占有するため最大FPSを直接下げる。`cacheGate` を保持したままのI/Oは、UIスレッド（idleの `Tick`）、キャッシュバー、idleワーカーを待たせる。

**【Bottleneck】**
CPU（syscall、ウイルス対策のminifilter）、同期（`cacheGate`・trackerの `gate`）、ファイルメタデータI/O。

**【Confidence】**
回数とsyscallの内容：**確認済み**（コードとトレース）。RTX 3060機での時間配分：**強い推定**（フォントのみのプロジェクトでKeyGeneration 2.1 ms）。

**【Proposed change】**
1. `StillCurrent(pending, files)` に分け、ファイルの再解決は最終採用直前（ストアへの `PutOwned` 直前と出力差替え直前）だけにする。途中は世代・リビジョン・親シーン・動的provider・描画環境（文字列比較）だけを確認する。miss 1フレームの検証は8回から2回（`TryAcquire`＋次Updateでのcommit）、GPU hitは3回から1〜2回になる。
2. `TryAcquire` で、ハッシュ計算をしなかった場合は2回目の `TryStamp` と末尾の `VerifyPaths`（直前に同じパスでopenしたばかり）を省く。NTFS判定はボリュームシリアルごと、祖先のreparse判定はディレクトリごとに短時間キャッシュする。最終commit時の検証は従来どおり全段を確認する。
3. `StillCurrent` 内で毎回SHA-256の鍵を2本作り直すのをやめ、`KeyEnvironment(context)` の文字列を比較する。他の入力は `Pending` の不変フィールドで、変われば `Validate` が落ちるので等価。
4. （設計変更 C3）開いたハンドルで状態を監視し、ディレクトリ変更を通知で受ける方式にして、hit時の検証をO(1)にする。

**【Risk】**
途中チェックでのパス別名（親ディレクトリのrename・junction差替え）の検出が、最終チェックまで遅れる。leaseのハンドル（`FileShare.Read`、Delete共有なし）を開いている間は、NTFSが親ディレクトリのrenameを拒否する可能性が高く、その場合は最終チェックも冗長になる。`FileLeaseChecks` にテストを追加して確かめる。キャッシュの内容と採用条件は変わらない。

**【How to verify】**
`PreviewPerformance` に `DependencyValidation`（回数・ファイル数・ms）を追加し、trace OFFで比較する。HostCacheProbeの `PreviewPerformanceChecks` を cold／RAM hit／GPU hit で、RTX 3060上の変更前後で比べる（p50/p95/p99）。素材1・10・50個のfixtureでファイル数依存を見る。Process MonitorかWPR（File I/O）で、再生中の `CreateFile` 回数／秒が数十分の一になることを確認する。画素一致・編集・Undo・素材上書きの既存テストを全て通す。

### F2 保存されるmissフレームで合成グラフを2回評価／毎フレームGPU資源を生成

**【Finding】**
`BeginPreviewReadback` は host の出力 command list を viewportサイズのtargetへもう一度 `DrawImage` する。playerの `Draw` も同じ出力を描くため、保存対象フレームでは合成グラフ（エフェクト・文字・動画テクスチャ）が2回評価される。さらに毎フレーム、D2D targetとD3D11 stagingを新規に生成・破棄する。

**【Evidence】**
`TimelineFrameCache.cs:1207`（`CreateBitmap`）、`:1217`（`DrawImage(output)`）、`EndDraw`、`:1233`（`CreateTexture2D`）、`:1235-1236`（`CopyResource`／`Flush`）。hit時に表示へ使う `UploadPreview`（`:1512-1568`）は「viewport画素のbitmapを逆変換で1:1に描くcommand list」で、画素一致が検証済み（`FramePixelChecks`、`docs/NONBLOCKING_READBACK_RESULTS_2026-10-02.md`）。idle（`CapturePreview`）と出力キャッシュ（`Capture`、`:1132`）も同じ再描画をする。

**【Execution frequency】**
採否判定を通った保存フレームごと。GPUが速ければ、再生中のmissはほぼ毎フレーム該当する。idleは全フレーム。

**【Why it matters】**
エフェクトが重くGPU律速のプロジェクトでは、GPU時間が最大約2倍になる。D2DのCPU側処理（effect graphの計画・定数更新・draw発行）も2倍になる。資源の生成と破棄は、ドライバーでの割当、初回 `Map` でのページフォールト（8 MBで約2000ページ）、VRAMの断片化を招く。

**【Bottleneck】**
GPU、CPU（D2D／ドライバー）、VRAM割当。

**【Confidence】**
二重評価：**確認済み（コード）**。大きさ：**要計測**。CIの `BeginGpuCopy` p50 8.8 ms／p95 397 msはWARP（GPU処理＝CPU処理）の値で代表性がない。RTX 3060の当時値0.29 msはCPU側だけ。

**【Proposed change】**
1. **一度だけ描く**：targetへ描いた直後に、そのtargetを逆viewport変換で1:1に描くcommand listで `TimelineSource` の出力を差し替える（hitと同じ `CommitReplacement` 経路）。同時に `RetainUploaded` でGPU保持の候補にする。playerの `Draw` はblitだけになる。
2. viewport寸法・形式ごとに、target bitmapとstaging（2〜3枚のリング）を使い回す。

**【Risk】**
出力差替えはhit経路が既に使っている契約で、画素一致テストがある。表示中はtargetを保持するため、VRAMが約1フレーム分増える。`Update` と `Draw` の間でviewportが変わる競合はhit経路と同等。GPU予算の計上（`Reserve`）は修正が必要。

**【How to verify】**
D3D11 timestamp query（§7.4）で、プラグイン側の描画とplayer `Draw` のGPU時間を比べる。PresentMonで、エフェクトの重いfixtureの表示間隔p50/p95/p99を比べる。`FramePixelChecks` を全て通す。

### F3 readback回収が描画スレッドに残す費用（8 MB LOH確保・ゼロ埋め・memcpy）

**【Finding】**
非待機 `Map` の完了後、次の `Update` 冒頭で、`new byte[]`（LOH）の確保とゼロ埋め、行ごとのmemcpyを描画スレッドで行う。

**【Evidence】**
`ReadPreviewReadback`（`TimelineFrameCache.cs:1279-1316`、確保は `:1298`）。`CompleteDeferred` は `CachePrefix` の冒頭（`:379`）から呼ばれる。RTX 3060：CpuMemcpy p50 1.83 ms。WARP：CpuAllocation p50 0.65 ms、100フレームで確保843 MB、GC回数 [3,2,2]（gen2が2回）。

**【Execution frequency】**
保存フレームごと。

**【Why it matters】**
描画スレッドで2〜3 ms／フレーム（1080p）。毎秒数百MBのLOH確保がgen2 GCを誘発する（大部分は並行GCだが停止区間がある）。RAMキャッシュは最大2 GiBの `byte[]` を抱え、LOHが断片化する。

**【Bottleneck】**
CPU（memcpy・ゼロ埋め）、GC、メモリ帯域。

**【Confidence】**
費用：**確認済み**（実測）。GC停止の影響：**要計測**。

**【Proposed change】**
1. 即時：`GC.AllocateUninitializedArray<byte>`（readback・`Capture`・ディスク読込の3箇所）。ヘッダーと全画素は必ず上書きされるので安全。
2. 計測後：stagingのリングを使い、`Map` は描画スレッド、memcpyはworker、`Unmap` は次の `Update` で行う。
3. 設計：VRAMを一次キャッシュにし、CPUへの読み戻しを遅延させる（C1）。

**【Risk】**
1は低い。2は `Map` 中のテクスチャを再利用しない所有権管理が必要。

**【How to verify】**
`CpuAllocation`／`CpuMemcpy` を比べる。`GC.GetTotalPauseDuration()` の再生前後の差分と、dotnet-countersの `gen-2-gc-count`・`time-in-gc` を見る。

### F4 idleの複製ソースがライブ用キャッシュフックを通る

**【Finding】**
idleの `source.Update` も `TimelineFrameCache.Prefix` を通る。複製の `TimelineSource` 用に新しい `SourceState`（＝3つ目の `KeyDependencyTracker`）を作り、バッチ初回に全体記述（200アイテム以下はinline、それ以上は `Task.Run`）、毎フレームのキー生成、`StillCurrent`×2を行う。複製にはplayerとの対応がなく `viewport == null`、つまり `cacheKey == null` なので、結果は一切使われない。

**【Evidence】**
`IdleFramePreRenderer.cs:292` → `TimelineFrameCache.cs:402`（`sources.GetValue(... new SourceState(scene))`）・`:404`（`background: preview`）・`:407-408`（viewport null）・`:486-489`（miss計上）・`:520`／`:532`。トレース（`gui-admission`）：idleの `Update` は p50 2.23 msのうち、本体0.37 ms、KeyGeneration 0.78 ms、検証0.86 ms（n=577）。結果の内訳はrender 556件で、ツールの「新規描画」統計（`RenderTimes`・`misses`）と `TotalUpdate` を汚している。

**【Execution frequency】**
idleの全フレーム。全体記述はバッチごと。

**【Why it matters】**
idleの `Update` 時間の約74%が無駄（CI）。200アイテムを超えるプロジェクトでは、バッチごとに背景で全体記述が走る（CIで1000アイテム約0.9秒）。これは再生開始後も走り続けることがある。

**【Bottleneck】**
CPU、ファイルメタデータI/O。

**【Confidence】**
**確認済み**（コードとトレース）。

**【Proposed change】**
idleが作るソースを「プレビューキャッシュ対象外」として登録し、`Prefix`／`CachePrefix` の先頭で即returnする（§8.3）。`FrameRenderReadiness` は別パッチなので、`WasLastUpdateReady` は従来どおり働く。

**【Risk】**
低い。idleは `TryPrimeCore` で明示的に保存するため、機能は変わらない。

**【How to verify】**
idleのフレーム数／秒と、範囲全体のキャッシュ完了時間を比べる。トレース上でidleスレッドの `KeyGeneration` が0件になることを確認する。

### F5 idle先読みの構造的な非効率

**【Finding】**
30フレームごとに複製環境を作り直し、バッチ間に250 msタイマーの空白が入り、readbackは同期で、検証が重複している。

**【Evidence】**
- バッチごと：`Json.LoadFromText<ModelSnapshot>(initial.Model)`（`:249`）、`CloneScene`、`new KeyDependencyTracker`（`:252`、初回captureで全体をinline記述）、`new TimelineSourceAndDevices`（`:253`。動画ごとのデコーダー初期化と初回seek）、`LongRunning` スレッド（`:214`）。
- フレームごと：`TryCapturePair`（`TryCapture`×2、`Validate`×2）、`Validate`×2（`:281`）、`TryPrimeIfCurrent`（`Validate`×2）、`TryPrimeCore`（`TryCapture` を再度、`Validate`）、同期の `CapturePreview`（`FinishPreviewReadback` = `MapFlags.None`）。
- トレース：30フレームのバッチ346〜838 ms、本体 `Update` 合計67〜180 ms。バッチ間の空白は44〜500 ms。

**【Execution frequency】**
停止中、範囲（既定はタイムライン全体）のキャッシュが完了するまで。

**【Why it matters】**
「全フレームhitの再生」に必要な事前生成にかかる時間を決める。動画が多いプロジェクトでは、30フレームごとのデコーダー初期化・seekと全体記述が支配的になり得る（要計測）。同期readbackは、CPUとGPUの処理を毎フレーム直列化する。

**【Bottleneck】**
CPU・ディスク（記述・デコーダー初期化）、同期（GPU待ち）、スケジューリング（タイマーの空白）。

**【Confidence】**
構造：**確認済み**。重いプロジェクトでの大きさ：**要計測**。

**【Proposed change】**
1. 即時：バッチが正常に終わったら次のバッチを直ちにスケジュールする。
2. 設計（C2）：モデルのキーが変わらない間、`cloneScene`・複製tracker・`TimelineSourceAndDevices` をバッチ間で保持する（編集・視点変更・再生開始で破棄）。captureと `Validate` の重複をまとめる。readbackを2〜3枚のリングで非同期化し、GPU処理と次フレームのCPU処理を重ねる。

**【Risk】**
保持する間、デコーダー・デバイス・ファイルハンドルが残る。再生開始時に破棄すれば再生へは影響しない。キャンセルの粒度は1フレームのまま。

**【How to verify】**
範囲のキャッシュ完了時間、フレーム数／秒、バッチ間の空白の分布を見る。キャンセルから再生の最初のフレーム表示までの時間も測る。

### F6 ディスク層：再生中のwrite-throughと読込スループット

**【Finding】**
保存した全フレームを、その場でディスクへも書く（Brotli q0＋SHA-256＋書込）。ワーカーは通常優先度の1本で、読込も同じワーカーで処理する（展開＋SHA-256照合）。

**【Evidence】**
`FrameCacheStore.Put` → `QueueWrite`（`FrameCacheStore.cs:236-243`）、`ProcessWrite`（`:528-575`、圧縮は `:545`、SHA-256は `:557`）、`ProcessRead`（`:470-516`、展開は `:498`、照合は `:505`）、スレッド生成は `:63`。CI：disk-write p50 15.6／p95 159 ms、disk-read p50 16.9 ms（展開11.0、checksum 4.7）。CIの再生（2コア・WARP）では、OFF時の本体 `Update` p50 1.03〜1.33 sに対し、ON時はcold 1.40〜1.62 s、warm 1.74〜1.92 s。`HostRender` もbypass 885 msに対しrender 1448 ms（`nonblocking/ram-64`）。

**【Execution frequency】**
書込は保存フレームごと。読込はReadAheadと停止中の参照ごと。

**【Why it matters】**
(a) 再生中、デコーダーとCPUコアを取り合う。(b) 1本のworkerは非圧縮的な1080pフレームで1枚あたり数十ms（展開＋SHA-256）かかり、60 fpsのディスク供給に届かない可能性がある。

**【Bottleneck】**
CPU、ディスク、スケジューリング。

**【Confidence】**
費用：**確認済み**（CI）。RTX 3060機での競合の大きさ：**要計測**（CIの差は2コア・WARPの影響が大きい）。

**【Proposed change】**
1. 即時：書込処理中はスレッド優先度を `BelowNormal` にする（読込はNormal）。
2. 計測後：再生中は書込を保留し、停止中に吐き出すか、RAMからの追い出し時に書く方式を検討する。整合性検査をSHA-256からXxHash128（System.IO.Hashing）へ変える（スキーマ更新が必要）。直近のフレームが圧縮できなければ圧縮の試行を省く。読込の展開と検査を並列化する。

**【Risk】**
優先度の変更は低リスク（読込は読込優先のキューで先に処理される）。ハッシュの変更は永続形式とDLL依存が変わる。

**【How to verify】**
ディスク書込を一時停止した状態とのA/Bで、再生のp95/p99フレーム時間を比べる。ディスクhitの最大持続fpsを測る。

### F7 起動ごとに素材の全内容をSHA-256で指紋化

**【Finding】**
素材の指紋はファイル全体のSHA-256（ファイルあたり最大4 GiB）。キャッシュはプロセス内（共有512件のFIFOとtrackerごとの辞書）だけで、YMM4を起動するたびに全素材を読み直す。

**【Evidence】**
`KeyDependencyTracker.StartFingerprinting`（`:322-345`）→ `FingerprintSafely` → `FileDependencyLease.TryAcquire(..., MaximumFingerprintBytes)`（`:61-72`、64 KB単位で読込とハッシュ）。

**【Execution frequency】**
プロジェクトを開いて最初にキャッシュが使われたとき（ファイルごとにプロセス内で1回）。

**【Why it matters】**
数GBの動画素材を持つプロジェクトでは、背景で数秒〜数十秒の連続読込が起き、最初の再生・seekのデコーダー読込と競合する（HDDでは顕著）。検証が終わるまで、そのファイルを使うフレームは通常描画になる。

**【Bottleneck】**
ディスクI/O、CPU。

**【Confidence】**
挙動：**確認済み**。体感への影響：**要計測**。

**【Proposed change】**
(volume serial, file ID, size, LastWriteTime, ChangeTime) → hash の永続DBを持つ。指紋化タスクは低I/O優先度で走らせる（`THREAD_MODE_BACKGROUND_BEGIN` か、`FileIoPriorityHintInfo` のlow）。

**【Risk】**
タイムスタンプを偽装した上書きは検出できない（`ChangeTime` は通常のWin32 APIでは変更できない）。脅威モデルとして明記する。

**【How to verify】**
プロジェクトを開いてから最初の再生までの、ディスク読込量とフレーム時間p99を比べる。

### F8 編集後の全体記述が描画スレッドでinline実行される

**【Finding】**
200アイテム以下のプロジェクトは、所要時間にかかわらず、描画スレッドで全体記述を行う。

**【Evidence】**
`KeyDependencyTracker.DescribeInline`（`:255-257`）。過去の実測（`c8de414` の引継ぎ）では100図形で73 ms（CI、streaming split前）。

**【Execution frequency】**
編集が落ち着いた後（settle 250 ms後）の最初の `Update`。

**【Why it matters】**
編集してから表示されるまでの間にスパイクが入る（数十〜百数十ms、要計測）。

**【Bottleneck】**
CPU（描画スレッド）。

**【Confidence】**
**要計測**（streaming split後の現行の値が未知）。

**【Proposed change】**
inline実行は、直前の記述が数ms以下だった場合に限る。それ以外は背景で記述し、その間は通常描画する（編集直後のフレームはどのみち描き直しになる）。

**【Risk】**
無関係なフレームを再利用できるまでの時間が少し延びる。

**【How to verify】**
編集から表示までのlatency p50/p95を比べる。

### F9 ReadAhead：GPU hitで実行されない／毎フレーム鍵を全て再計算

**【Finding】**
(a) GPU hit（とlive）は `ReadAhead` より前でreturnするため、GPU保持の区間を再生している間は、その先のディスク上のフレームを先読みしない。(b) 再生中は毎フレーム、15〜120個の鍵を `ComposeKey`（SHA-256）で作り直す。

**【Evidence】**
`TimelineFrameCache.cs:458-464`（GPU hitのreturn）と `:476`（ReadAhead）。`ReadAhead`（`:637-660`）。

**【Execution frequency】**
(a) GPU保持区間の再生中。(b) 再生中の毎フレーム。

**【Why it matters】**
(a) GPU保持の区間が終わった直後にディスクのみのフレームが続くと、miss（通常描画）が連続する。(b) 推定0.1〜0.6 ms／フレーム。

**【Bottleneck】**
(a) キャッシュの供給。(b) CPU。

**【Confidence】**
(a) **確認済み（コード）**、影響は条件次第。(b) 費用は推定。

**【Proposed change】**
GPU hit時も、再生中なら `ReadAhead` を呼ぶ。鍵は `(frameKey, frame)` で記憶し、窓に新しく入ったフレームだけ計算する（`TryGetPreviewResidency` の `StatusKeys` と同じ方式）。

**【Risk】**
低い。

**【How to verify】**
GPU区間からディスク区間へ続く再生で、hit率とフレーム時間を見る。

### F10 機能OFF時のフック費用

**【Finding】**
設定に関係なく、起動時に全フックが入る。

**【Evidence】**
`FrameCacheToolPlugin` のコンストラクタ → `HostIntegration.EnsureInstalled` → `TimelineFrameCache.TryInstall`（`HostIntegration.cs:90`）。毎フレームの処理は §3.1 のとおり。

**【Execution frequency】**
毎 `Update`・`Draw`・動画ソースの `Update`。

**【Why it matters】**
RTX 3060のOFF時 `TotalUpdate` p50 0.157 ms（フック込み）、`TotalPreview` 0.289 ms。フックの純増は推定数十µs／フレームで、体感の原因である可能性は低い。

**【Bottleneck】**
CPU（reflection・小さな確保）、GC（`WeakReference` とCWTのハンドル）。

**【Confidence】**
処理の存在：**確認済み**。大きさ：**強い推定（小）**。YMM4単体とのA/Bは未実施。

**【Proposed change】**
プレビューと出力キャッシュの両方がOFFなら、`ObservePlayer` と `FrameRenderReadiness` のデコーダー判定を早期returnする。`updateMeasurements.Remove+Add` は `AddOrUpdate` に置き換える。

**【Risk】**
ONにした直後の最初のフレームは判定できない（保存しないので安全側）。

**【How to verify】**
プラグインなし／導入・OFF／ONで、`PreviewPerformanceChecks` と PresentMon を比較する（§7）。

### F11 wideフレームはプロジェクト全ファイルに依存する

**【Finding】**
シーンアイテムや音声波形（AudioSpectrum）を含むフレームは、プロジェクト全体の素材（`globalFiles`＋全アイテム＋入れ子）を検証対象にする。

**【Evidence】**
`FrameDependencyIndex.Compute`（`:97-108`、`IsWide` で `Whole` を返す）、`FrameCacheKey.cs:484`。

**【Execution frequency】**
該当フレームの毎 `Update`。

**【Why it matters】**
F1の費用がプロジェクトのファイル数倍になる。256ファイルを超えるとbypassになる。

**【Bottleneck】**
F1と同じ。

**【Confidence】**
**確認済み（コード）**。

**【Proposed change】**
F1の修正で大半が解消する。将来は、シーンアイテムが参照するタイムラインに限定する。

**【Risk】**
—

**【How to verify】**
シーンアイテムを含むfixtureで、F1の計測を行う。

### F12 立ち絵を含むフレームは常に通常描画（適用範囲）

**【Finding】**
`TachieItem` を含むフレームは常にキャッシュ対象外。

**【Evidence】**
`FrameCacheKey.cs:153-158`。

**【Execution frequency】**
—

**【Why it matters】**
立ち絵を全編に表示する一般的なゆっくり解説動画では、ほぼ全フレームでキャッシュの恩恵がない。対象外の判定はファイル検証の前に行われるので、追加費用は小さい。性能バグではないが、効果を期待する前提として重要。

**【Bottleneck】**
—

**【Confidence】**
コード：**確認済み**。該当するかはユーザーのプロジェクト次第（ツールの「対象外」件数で確認できる）。

**【Proposed change】**
仕様としてユーザーに示す。将来、口パクに使う音声と重ならない区間を対象にできるかは、ホスト契約の調査が要る。

**【Risk】**
—

**【How to verify】**
対象プロジェクトで、ツールの「対象外」と「新規描画」の比率を見る。

### F13 出力キャッシュONは同期captureでCPUとGPUを直列化する

**【Finding】**
出力キャッシュの保存は `Map(Read)` でGPU完了を待つ。2回目の描画、LOH確保、ディスク書込も伴う。

**【Evidence】**
`TimelineFrameCache.cs:522-528` → `CaptureScene` → `Capture`（`:1111-1163`）。

**【Execution frequency】**
出力キャッシュON時の書出しで、missフレームごと。

**【Why it matters】**
初回の書出しは、OFFより遅くなる可能性が高い（CPUとGPUの重なりを失う）。利点は同じ内容を再度書き出すときだけ。

**【Bottleneck】**
同期、GPU、CPU。

**【Confidence】**
待機：**確認済み**。書出し速度への影響：**要計測**。

**【Proposed change】**
ライブと同じ非同期readbackのリングを使う。それまでは、出力キャッシュは再出力時だけ有効にすることを推奨する。

**【Risk】**
—

**【How to verify】**
NVENC書出しの所要時間と1フレームあたりの時間を、OFF／ON cold／ON warmで比べる。

### F14 計測自体の偏り

**【Finding】**
詳細ログ（trace）ONでは、全プロセッサの `Update`／`Draw` にHarmonyのdetourが入る。呼出しごとに文字列連結（`GetType().FullName + Identity(...)`）を行い、記録ごとにlockを取る。時間はStopwatchによるCPU wall timeだけ。

**【Evidence】**
`ProcessingTraceHooks.cs:71-72, 111-112`、`CacheTrace.cs:128-140`。

**【Execution frequency】**
trace ON時の全プロセッサ呼出し。

**【Why it matters】**
CIトレースの絶対値は膨らんでいる。比較に使えるのは、同じtrace条件どうしの相対値だけ。GPU時間は測れていない。

**【Bottleneck】**
—

**【Confidence】**
**確認済み**。

**【Proposed change】**
時間比較はtrace OFFで、`PreviewPerformance` とPresentMonを使う。原因の帰属を調べるときだけtraceを使う。GPU timestamp queryを追加する（§7.4）。

**【Risk】**
—

**【How to verify】**
—

### F15 メモリ監視が毎秒、全プロセスのスナップショットを取る

**【Finding】**
`Process.GetCurrentProcess().PrivateMemorySize64` を毎秒呼ぶ。Windowsでは全プロセス・全スレッド分の情報を取得するAPIを呼ぶ。

**【Evidence】**
`CacheMemoryController.cs:65-73`（`ReadSnapshot`、Timerは `:25`）。

**【Execution frequency】**
毎秒（ストアの生成後）。

**【Why it matters】**
スレッドプール上で、数ms・数百KBの確保が周期的に起きる。小さい。

**【Bottleneck】**
CPU。

**【Confidence】**
**確認済み**（.NETの実装）。影響は小。

**【Proposed change】**
`GetProcessMemoryInfo`（`PROCESS_MEMORY_COUNTERS_EX.PrivateUsage`）に置き換える。

**【Risk】**
低い。

**【How to verify】**
—

### F16（仮説）hitが本体 `Update` を飛ばすことによるデコーダー不連続

**【Finding】**
hitでは本体 `Update` を実行しないため、デコーダーの位置とホストの約1秒先のprefetchが進まない。キャッシュ済み区間の後の最初のmissで、seekや冷えた初期化が起きる可能性がある。

**【Evidence】**
`CachePrefix` がhitで `false` を返す。prefetchについてはコメント（`FrameRenderReadiness.cs:259-262`）。既存トレースでは、hit直後のmiss（n=5〜7）がmiss直後のmissより遅いという結果は**出なかった**。

**【Execution frequency】**
キャッシュ済み区間から未キャッシュ区間へ移るたび。

**【Why it matters】**
確認できれば、境界でのstutterの原因になる。

**【Bottleneck】**
デコード（CPU／ディスク）。

**【Confidence】**
**要計測（仮説）**。

**【Proposed change】**
確認できた場合のみ：read-aheadで次のmissが分かったら、直前のK枚はhitにせず本体で描く（ウォームアップ）。

**【Risk】**
—

**【How to verify】**
動画12本のfixtureで、キャッシュ済み区間とそうでない区間を交互に置き、境界直後のmissフレーム時間を測る。

### その他（小さく、優先度は低い）

- `pickerType.GetFields(Instance)` が毎 `Update` で配列をコピーする（`TimelineFrameCache.cs:397`）。
- ストアの照会ごとに `ToLowerInvariant`、`ValidKey` はLINQの `All` を使う（`FrameCacheStore.cs:81, 129, 145, 224, 764`）。キャッシュバーは250 msごとに最大数千件をストアのlock内で照会する。
- 編集直後、キャッシュバーの `TryPeekFrameKeys` がtrackerの `gate` 内で全区間の鍵を再計算する。描画スレッドの `TryCapture` が待たされ得る。
- `ObservePlayer` が `Draw` ごとに `WeakReference` を3個確保する。

---

## 5. すでに良くできている部分（触らない方がよい）

- **ライブの非待機readback**（`Map(DoNotWait)` のpoll、sourceごとに保留1枚、処理中は保存を省略）。同期版のMapWait p50 420 msを0.01 msにした。残る費用はF2・F3で、この設計自体は正しい。
- **ディスクI/Oを描画スレッドから完全に分離**。読込優先のキュー、128件／128 MiBの上限と超過時の破棄、原子的なrename、purgeの世代永続化。
- **GPU保持の頻度ベースadmission**と、COM参照を分けた借用の寿命管理。順次スキャンが常駐フレームを追い出さない。
- **編集中のsettle（250 ms）と、大規模プロジェクトの背景記述**、JSONのstreaming split（`8a4a2e9`）、フォントファミリーの30秒キャッシュ、区間ごとの依存とキーのキャッシュ。
- **NVENC出力**：CPU readbackなしのGPUテクスチャ渡し、async depth 4、所有テクスチャへのコピー、writerスレッドと上限付きキュー。`EncoderThread.Invoke` のスレッド切替は1フレームあたり数十µs程度で、問題ではない。
- **常時ONの計測**（`FrameTimeSamples` の固定リング）は十分軽い。traceはOFFなら `Volatile.Read` 1回で抜ける。
- RAM予算を自動調整し、GCを強制しない。

---

## 6. 改善候補の優先順位

**この順番の理由**
1. まずF1を除く。キャッシュONの全経路に効く純粋な無駄で、採用条件を変えずに消せる。これが残っている間は、他の変更の効果がこのノイズ（p95で数十ms）に埋もれる。
2. 次にidleの効率（F4・F5）。キャッシュの価値は「再生前にどれだけ埋まっているか」で決まり、ここは安全に数倍化できる見込みがある。
3. その後、missの費用（F2・F3）をGPU timestampで測ってから削る。
4. ディスク層とVRAM層は設計変更なので最後に回す。

### A. 安全にすぐ変更できる

| # | 変更 | 対象 | 期待 |
| --- | --- | --- | --- |
| A1 | 検証を最終採用直前の1回に減らす。途中は安価な確認のみ。環境は文字列で比較 | `TimelineFrameCache.StillCurrent` と呼出し11箇所、`KeyCapture.Validate` | miss 1フレームの検証8回→2回、GPU hit 3回→1〜2回 |
| A2 | `DependencyValidation` の計測stageを追加（A1の検証用） | `PreviewPerformance`、`FileDependencyLease` | trace OFFで比較できる |
| A3 | idleが作るソースをキャッシュ対象外にする | `TimelineFrameCache.CachePrefix`／`Prefix`、`IdleFramePreRenderer` | idleの `Update` 費用 約−70%（CI） |
| A4 | `GC.AllocateUninitializedArray` | `ReadPreviewReadback`、`Capture`、`ProcessRead` | 約0.5〜1 ms／保存フレーム |
| A5 | ディスク書込中は `BelowNormal` | `FrameCacheStore.DiskWorker` | 再生中の競合を軽減 |
| A6 | GPU hit時もReadAheadを呼ぶ（再生中） | `CachePrefix` | GPU区間→ディスク区間のhit率 |
| A7 | idleバッチの即時継続 | `IdleFramePreRenderer.RenderBatch` | バッチ間の約200 msを除く |
| A8 | メモリ監視APIの置換 | `CacheMemoryController.ReadSnapshot` | 小 |

### B. 計測してから変更する

| # | 変更 | 先に測るもの |
| --- | --- | --- |
| B1 | 一度だけ描いて表示を差し替える（F2） | GPU timestamp、PresentMon、画素一致 |
| B2 | target／stagingのリング化（F2） | 生成・破棄の時間、初回 `Map` のページフォールト |
| B3 | `TryAcquire` のメタデータキャッシュ（F1-2） | 親ディレクトリrename拒否の挙動テスト |
| B4 | 再生中の書込保留、XxHash、圧縮の試行省略、読込の並列化（F6） | ディスク供給fps、書込停止時とのA/B |
| B5 | inline記述の条件変更（F8） | 編集から表示までのlatency |
| B6 | memcpyを描画スレッド外へ（F3） | `CpuMemcpy`、GC停止時間 |
| B7 | 出力キャッシュの非同期readback（F13） | 書出し時間の比較 |
| B8 | デコーダー不連続の対策（F16） | 境界直後のmiss時間 |
| B9 | 機能OFF時の早期return（F10） | YMM4単体／導入・OFFのA/B |

### C. 設計変更が必要

- **C1 VRAMを一次キャッシュにする**：描いたbitmap（B1）をそのままGPUフレームとして保持し、予算は `IDXGIAdapter3::QueryVideoMemoryInfo` の `Budget` と `CurrentUsage` から動的に決める。CPUへの読み戻しはVRAMから追い出すときか停止中に遅延して行う。RTX 3060（12 GB）なら数百フレームをVRAMに置け、miss経路からmemcpyとLOHが消え、hitはほぼ無料になる。前提：B1と、デバイスロスや他アプリの圧迫への対応。
- **C2 永続idleセッション**：clone・tracker・`TimelineSourceAndDevices` をモデルが変わるまで保持する。readbackは非同期リング、バッチ間の空白はなくす。任意で、CPU律速のプロジェクト向けに独立したデバイスで2セッションを並列に走らせる（VRAMとデコーダーのメモリは2倍）。
- **C3 イベント駆動の依存検証と、永続的な指紋DB**：開いたハンドルでstampを確認し、ディレクトリ変更を通知で受けて世代を上げる。hit時の検証は世代の比較だけになる（F1-4、F7）。
- **C4 Cache Before Playback（AEのRAMプレビュー相当）**：ユーザーの操作で、範囲をC2により最大速度で生成し、キャッシュバーで進捗を示し、完了（または残り時間＜再生時間）してから本体の再生を開始する。音声時計には触れない。再生中の `Update` では決して待たない。前提：playerの再生開始のホスト契約（`StopAsync` がゼロへseekする、`EndAudioTask` が開始位置へ戻すなど）を読むこと。
- **C5**（GCが問題と分かった場合のみ）参照カウント付きのネイティブメモリでフレームを管理する。

### D. 触らない方がよい

- **再生中に `Update` 内で描画完了やディスクを待つこと**（音声時計は止まらないので悪化する）。`PausedDiskWait` の延長も同様。
- 同じ `TimelineSource` や同じD2D contextに対する並列Update（MFR）。
- NVENCのasyncパイプラインと、非待機readbackの基本設計。
- キーのSHA-256（µs単位で、費用の主因ではない）。
- C1の予算取得なしにGPU保持の予算だけを大きくすること。

---

## 7. 計測計画（最小で有効なベンチマーク）

### 7.1 条件（3条件＋キャッシュ状態）

| 記号 | 条件 |
| --- | --- |
| C0 | YMM4のみ（`.ymme` をアンインストール） |
| C1 | 導入・全機能OFF（プレビューキャッシュOFF、出力キャッシュOFF。NVENCは既定ON） |
| C2 | プレビューキャッシュON・cold（purge直後、idle OFF） |
| C3 | ON・warm RAM（idle完了後、RAM上限は十分） |
| C4 | ON・warm disk（RAM 64 MiB） |
| C5 | ON・GPUループ（16フレームの区間を反復） |

### 7.2 fixture

- **L**（固定費）：既存の `PreviewPerformanceChecks`（100図形＋Arial、1080p）。
- **N**（ファイル数）：1080p60・10秒。同時に表示する画像を1・10・50個にし、日本語フォント3種のテキストを加える → F1のファイル数依存。
- **E**（GPU律速）：1080p60・10秒。20レイヤーの画像・文字にぼかし・グロー・影 → F2。
- **V**（デコード律速）：既存の `prepare-stress-project.ps1`（30秒、同時12本の動画＋文字300）→ F6・F16。
- **W**（wide）：Nにシーンアイテムを1つ追加 → F11。

### 7.3 シナリオと指標

| シナリオ | 指標 |
| --- | --- |
| S1 連続再生（30秒、先頭から） | 表示間隔（PresentMon `MsBetweenDisplayChange`）の平均・中央値・p95・p99・最大・標準偏差、stutter数（中央値の2倍超）、drop数（`Update` に渡された時刻のフレーム番号の飛び）、`Update`／`Draw` のCPU時間（`PreviewPerformance`） |
| S2 scrub（停止中に20箇所seek） | seek latency（`Timeline.CurrentFrame` 変更から新しいフレームのPresentまで） |
| S3 再生開始 | TTFF（再生操作から最初の新フレーム表示まで）。idleジョブ実行中と非実行中を比べる |
| S4 idle充足 | 範囲100%までの時間、フレーム数／秒、バッチ間の空白 |
| S5 出力 | NVENC H.264書出しの所要時間と1フレームあたりの時間（出力キャッシュOFF／ON cold／ON warm） |

補助の指標：file I/O回数／秒（WPRのFile I/O、またはProcess Monitor）、GC停止の合計（`GC.GetTotalPauseDuration()` の前後差）、LOHの確保速度（dotnet-counters）、GPU時間（§7.4）。

手順：
- 時間の比較では詳細ログ（trace）をOFFにする（F14）。
- プレビュー窓のサイズと表示倍率を固定する（キーがviewportに依存するため）。
- 各条件でwarm-upを1回捨て、5回を順序ランダムで測る。
- 各回のp95・p99を出し、その中央値を報告する。

### 7.4 GPU時間（Stopwatchで推測しない）

計測モードだけで、D2D targetのD3D11 deviceに `TIMESTAMP_DISJOINT`＋`TIMESTAMP`×2 のqueryを作る。プラグイン側の描画（`BeginPreviewReadback` の `DrawImage`～`EndDraw`）とplayerの `Draw` を挟み、結果は数フレーム後に `GetData(DoNotFlush)` で回収する。D2Dはコマンドをまとめて提出するので、開始timestampの前に `ID2D1RenderTarget.Flush()` を入れる（計測モードだけの擾乱として許容する）。全体の把握にはPIX・GPUView（WPAのGPU queue）を併用する。

---

## 8. 次に実際に変更するコード

### 8.1 （A1）検証を最終採用直前の1回に

`KeyDependencyTracker.cs` の `KeyCapture`：

```csharp
// files: re-resolve the leased paths. The lease's open handles (FileShare.Read, no delete sharing) already deny
// writes, deletes and renames of the files themselves, so only the check right before a result becomes visible
// (the store commit, the output swap) needs it.
public bool Validate(bool files = true) => Volatile.Read(ref disposed) == 0 && tracker.ValidateRevision(Revision)
    && tracker.HasParents(parents) && DynamicCurrent() && (!files || (lease?.VerifyPaths() ?? true))
    && Volatile.Read(ref disposed) == 0;
```

`TimelineFrameCache.cs`：`Pending` に取得時の `KeyEnvironment(context)` を持たせ、`StillCurrent` を分ける。

```csharp
private static bool StillCurrent(Pending value, bool files = false)
{
    using var validation = CacheTrace.Measure("cache-state-validation");
    if (!EnabledFor(value.UsageKey == "Exporting") || value.Generation != Interlocked.Read(ref generation)) return false;
    bool valid;
    using (CacheTrace.Measure("capture-dependency-validation")) valid = value.Capture.Validate(files);
    if (!valid) return false;
    // LiveKey/CacheKey are functions of the immutable Pending fields and the context's render state; the latter is
    // the only input that can change here, so compare it instead of hashing both keys again.
    using var keys = CacheTrace.Measure("render-environment-key-validation");
    return KeyEnvironment(value.Devices.DeviceContext) == value.Environment;
}
```

呼出しの変更：

| 行 | 用途 | `files` |
| --- | --- | --- |
| `:520` | postfixの入口 | false |
| `:525` | 出力キャッシュの `PutOwned` 直前 | **true** |
| `:532` | `LastKey` 等の記録 | false |
| `:573` | `Deferred` への受け渡し（commitは後） | false |
| `:599`、`:604` | 回収の入口・保留 | false |
| `:612` | `PutOwned` 直前 | **true**（ファイル検証は `cacheGate` の外で先に行い、ロック内は安価な確認のみ） |
| `:1400`、`:1442` | hit経路の入口 | false |
| `:1421` | `CommitReplacement`（出力差替え直前） | **true** |

idle側（`IdleFramePreRenderer.cs:281`、`TryCapturePair`、`TryPrimeIfCurrent`）は `Validate(files: false)` にする。最終の `TryPrimeCore` の `capture.Validate()` は `true` のまま残す。

### 8.2 （B3）`TryAcquire` の重複syscallを削る

```csharp
// FileDependencyLease.TryAcquire（ループ内）
if (!TryStamp(file.SafeFileHandle, out var stamp) || !IsNtfsVolume(stamp.Volume, file.SafeFileHandle)) { ...; return false; }
...
if (!reused) { /* hash */ hashedAny = true;
    if (!TryStamp(file.SafeFileHandle, out var after) || after != stamp) return false; }
...
// After the loop: the handles were opened by these very paths a moment ago. Re-resolve only if hashing took time.
if (hashedAny && !candidate.VerifyPaths()) return false;

private static readonly ConcurrentDictionary<ulong, bool> ntfsVolumes = new();
private static bool IsNtfsVolume(ulong volume, SafeFileHandle handle) => ntfsVolumes.GetOrAdd(volume, _ => IsNtfs(handle));
```

`IsLocalPlainPath` の祖先チェックは、`TryAcquire` からの呼出しに限ってディレクトリ単位で短時間キャッシュする。最終commitの `VerifyPaths` は従来どおり全段を確認する。あわせて `FileLeaseChecks` に「lease保持中に親ディレクトリをrenameできないこと」のテストを追加し、結果に応じて最終検証の要否を決める。

### 8.3 （A3）idleのソースをキャッシュ対象外にする

```csharp
// TimelineFrameCache.cs
private static readonly ConditionalWeakTable<object, object?> privateSources = new();
// Sources the plugin renders itself (the idle pre-renderer's clone): primed explicitly by TryPrimeCore, never
// served or stored by the live preview hook, and not counted in the preview statistics.
internal static void ExcludeFromPreviewCache(object sourceOrOwner) =>
    privateSources.AddOrUpdate(GetTimelineSource(sourceOrOwner), null);

private static bool Prefix(object __instance, TimeSpan time, object usage, out UpdateMeasurement __state)
{
    bool own = privateSources.TryGetValue(__instance, out _);
    string name = usage.ToString() ?? string.Empty;
    __state = new UpdateMeasurement(!own && name is "Playing" or "Paused", time, name);
    ...
    __state.RunsHost = own || CachePrefix(__instance, time, usage, out var pending);
    ...
}
```

`IdleFramePreRenderer.cs:253` の直後に `TimelineFrameCache.ExcludeFromPreviewCache(source);` を追加する。`FrameRenderReadiness` は別パッチのため、`WasLastUpdateReady` は従来どおり働く。

### 8.4 （A4・A5・A6・A7）小さな変更

```csharp
// TimelineFrameCache.ReadPreviewReadback (:1298) / Capture (:1136), FrameCacheStore.ProcessRead (:488)
record = GC.AllocateUninitializedArray<byte>(checked(viewport.Width * viewport.Height * 4 + PreviewRecordHeader));
// The header (32/24 bytes) and every pixel row are written below; zeroing 8 MB first only costs time.

// FrameCacheStore.DiskWorker, before the switch:
Thread.CurrentThread.Priority = operation.Kind == OperationKind.Read ? ThreadPriority.Normal : ThreadPriority.BelowNormal;

// TimelineFrameCache.CachePrefix, GPU hit branch (:458) before return:
if (!paused && viewport is { } shown) ReadAhead(state, scene, time, usageKey, shown, playing: true);

// IdleFramePreRenderer.RenderBatch finally: after a batch that reached endOrdinal without cancellation
if (completed) OnUi(() => Tick(null, EventArgs.Empty)); // do not wait up to 250 ms for the next tick
```

### 8.5 （B1）一度だけ描く（スケッチ。GPU計測の後に実施）

```csharp
// BeginPreviewReadback: keep the drawing target alive in PreviewReadback (instead of `using var target`).
// StorePreview, after the readback was queued:
ID2D1CommandList? display = null;
try
{
    // Same command list UploadPreview records, but from the GPU bitmap that already holds this frame's pixels:
    // the player's Draw then blits it instead of evaluating the composition a second time.
    display = RecordViewportBitmap(pending.Devices.DeviceContext, readback.Target, viewport);
    lock (cacheGate)
        if (CommitReplacement(source, pending, display, bytes, null, expectedOutput: output))
        {
            RetainUploaded(pending, display, bytes);
            display = null;
        }
}
finally { display?.Dispose(); }
```

`CommitReplacement` は「差替え前の出力」が `pending.PreviousOutput` 固定なので、`expectedOutput` を引数に取る。GPU予算（`Reserve`）の付け替えが必要。画素一致は、hit経路と同じ `FramePixelChecks` で検証する。

---

## 付録：この調査で再集計した値

- `nonblocking/ram-64`、render経路（n=49）：`timeline-update` p50 1503.7 ms。うち `HostRender` 1448.0、検証7回で計7.7（p95 26.0）、KeyGeneration 2.26、BeginGpuCopy 8.8、deferred-store 2回で6.1。
- 同、bypass（キャッシュOFFの再生、n=39）：`timeline-update` p50 885.4 ms、`HostRender` 885.4。
- 同、GPU hit（n=7）：p50 5.59 ms。うち検証2回で2.2、KeyGeneration 2.9。
- `2026-10-01-gpu/gpu-hit`（n=100）：p50 1.76 ms。うち検証約0.8、KeyGeneration 0.79、borrow＋commit 0.013。
- `2026-10-01-gpu/gui-admission`、idleスレッド（n=577）：`Update` p50 2.23 ms。うち `HostRender` 0.37、KeyGeneration 0.78、検証0.86。出力はrender 556件。
- 再生のUpdate p50（OFF bypass／ON cold／ON warm）：ram-256 1332／1617／1795 ms、ram-64 1124／1405／1738 ms、nonblocking 1028／1592／1921 ms（2コア・WARP）。
