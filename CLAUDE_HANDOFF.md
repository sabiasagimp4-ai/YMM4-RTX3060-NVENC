# Claude 引継ぎ — YMM4 RTX3060 NVENC / AE風キャッシュ

更新日: 2026-10-01。これは **未完成の作業保存（WIP checkpoint）** です。製品完成・配布可能・AE完全再現を意味しません。

## 最初に読むこと

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
