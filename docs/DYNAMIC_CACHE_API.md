# 動的な計算・依存の契約

## 任意の計算結果

`DynamicComputeCache.Shared` は128 MiB・256 entryのprocess-localストア。画素以外の不変結果も登録できる。登録IDはclassとschemaを識別し、キーには全入力・時刻依存・隠れた状態・必要なdevice／contextを含める。大きい結果や容量不足でも要求consumerへは結果を渡し、保存だけ省略する。resident予算は借用中・計算中の全プロセス使用量ではない。

```csharp
using var computeClass = DynamicComputeCache.Shared.Register<string, byte[]>(
    "com.example.processor/lookup-table/v1",
    generateKey: input => input,
    compute: input => System.Text.Encoding.UTF8.GetBytes(input),
    approximateBytes: result => result.LongLength,
    delete: result => { },
    backgroundThreadSafe: true);

using var receipt = await computeClass.ComputeAsync("complete-input-token");
if (receipt is not null)
{
    ReadOnlyMemory<byte> result = receipt.Value; // Never mutate a shared result.
    long originalCostTicks = receipt.ComputeTicks;
}
```

- `CheckoutCached`: 即時参照。計算・I/O・待機・予約を開始しない。Missing／Computing／Readyを返す。
- `ComputeIfNeededAndCheckout`: 値がない場合なら呼出し側で計算。`waitForOtherThread=false` が待たないのは既存の別ownerだけ。AE SDKと同じno-waitの意味。
- `ComputeAsync`: background安全を明示したclassだけ。ownerを一つだけ予約し、他consumerは共通completion taskをawaitする。待機consumerごとにworkerをブロックしない。
- `isCurrent`: 要求開始・計算終了・引渡し時の依存確認。失敗した結果を保存せず、stale結果のreceiptも返さない。
- 計算失敗は参加consumerへ例外を伝え、次の要求は再試行できる。Failed結果の永続保存はしない。
- 取消はconsumerの待機を止める。共有計算を巻き添えにせず、取消後のowner結果も他consumerへ供給できる。
- receiptは必ずDispose。退避・Clear・登録解除はcacheの所有だけを外す。借用中の値は生存し、最後の参照でdeleterを一度呼ぶ。
- deleterは最後に解放したスレッドで呼ばれ得る。必要なら資源のowner contextへmarshalする。背景安全の宣言はcomputeだけでなく関連callbackも対象。
- class解除・Clear後に古い計算が完成しても、cacheへ復活しない。進行中計算も256件で制限し、purgeを繰り返して上限を抜けない。

同期の同一キー自己再帰は拒否する。複数キーを循環して同期waitする依存DAGはこのAPIだけで解決しない。ホストの可変D2D graphや同じTimelineSourceを並列で操作するためのAPIではない。

## 処理自身による依存報告

`ICacheDependencyProvider` をホストのanimatableツリー内の設定／効果へ実装する。プロジェクト記述時に発見し、要求時刻の `CacheDependencySnapshot` を既存フレームキーへ混入する。snapshotはclass・schema・状態・context・入力identity／状態token／時間範囲を持つ。keyframe／素材／外部状態の変化はtokenとIsCurrentで報告する。

`CanCaptureOnCurrentThread` の既定はfalse。処理側が実際のスレッド／contextでcallback可能と判断した場合だけtrueを返す。ライブ・idle・exportで同じとは限らない。失敗・例外・変更は通常描画へbypassし、保存・表示前にも再確認する。処理を観測しただけで信頼や安全宣言を作らない。既存のMVID・信頼・file leaseを緩めない。

現段階ではproject内で発見したproviderすべてを追加依存とする。ホストがcheckoutした最小依存範囲の自動追跡ではなく、keyframeの区間内最小無効化も未実装。providerのない処理には従来方式を維持する。UIの帯／disk先読みからcontext制約のあるproviderを呼ばないため、providerを含むprojectではそのキーの受動的な一覧を作らない。通常の要求・復元・保存は有効。

`CacheImageRequest.IsSatisfiedBy` はregion・bitdepth・channel・透明画素のRGB・format・contextの充足判定。任意の計算結果を再利用する側が呼ぶための契約で、現在の合成済みフレームを別viewportへ流用する機能ではない。

## 実際のフレーム経路への適用

`FrameCacheStore.TryGetCached` はRAMだけを副作用なしに読む。既存のTryGetはdisk予約可能な供給経路として維持する。

通常のplayer Update+Draw CPU経過時間をキー別に記録し、RAM復元を同じsource・画素サイズ帯で別に記録する。元のrender費用をhit費用で上書きしない。2回以上のrenderと8回以上のRAM復元で、render最大値が復元平均の半分未満の場合だけ、readback開始前・保存採用前に追加保存を見送る。未知の費用では保存する。GPU保持が有効なら、GPU復元を別に8回以上測り、GPUでも同じ2倍の費用不利が確認できるまで保存を維持する。GPU hit、disk待ち、別formatの未測定費用をRAM復元の学習に混ぜない。借用中や既存の有効な画像を捨てる判断ではない。

これらはCPU側の観測でありGPU実行時間ではない。Present、音声時計、UpdateとDrawの間の待機も含まない。idle sourceは標準playerのDrawを通らないため、未知の費用として従来どおり保存する。AEの内部価値算式を再現したとの主張ではない。

## ディスクと停止範囲

専用workerでBrotli quality 0の無損失圧縮を試し、1 KiB未満と12.5%以上縮まないレコードはrawへ戻す。圧縮は保存空間の判断で、実素材での速度優位を保証しない。`YMMFRZ01` も元の画素長・SHA-256を保存し、展開サイズ上限・stream終端・全入力消費・checksumを検証する。従来の `YMMFRM01` はそのまま読める。disk予算は物理長、先読み／RAM予算は展開後の長さで数える。codec作業配列はworkerの一時領域で最大1フレーム分が追加される。

disk読込・展開の観測費用から再生先読みを0.5〜2秒へ伸ばし、最大120 frameかつ半RAMの窓で制限する。deadlineや音声同期を調停するpreview schedulerは未実装。

停止中の先読み開始・終了フレームを設定できる。終了は含まず、0なら末尾。変更時は旧jobを取り消し、設定範囲内だけを3順序で巡回する。YMM4の選択範囲との自動連動は行わない。

## 検証

portable harnessで計算共有・取消・借用寿命・purge中の完成・失敗再試行・例外deleter・再帰・入力と時間とcontextのキー差・要求充足・費用判断・指定範囲・圧縮とrawの往復・破損拒否を検証する。Windows CIは実4.56.1.0で動的providerの発見と隠れた状態の変更、既存の編集／Undo／デコード／WARP画素一致・GPU保持を検証する。
