# GPU上の復元済みフレーム保持

CPU→描画資源へのCopyFromMemoryを、予算に収まる反復フレームで省く。
RAM/ディスクから初めて復元したプレビュー画像だけをGPU側に昇格する。
任意のエフェクト名に依存せず、既存の依存・完成判定で承認された画素を対象にする。

## 寿命と無効化

- 保存するのはUploadPreviewが作る、書換えないbitmapを参照するclosed command list。
  ホストの可変effect graphは保持しない。
- cacheと表示中のsourceはQueryInterfaceで別のCOM参照を持つ。
  LRUで追い出しても表示中のborrowを破棄しない。最後のowner解放で画像予算を返す。
- 退避対象はグローバルLRUから選び、追加は利用頻度で判断する。頻度が同じなら既存entryを残す。
  sourceごとの履歴は256キーに制限し、512観測ごとに頻度を半減して新しい範囲へ追従する。
  保持の予算は、描画に使うGPUのVRAMに応じた自動配分（初期128MiB）か、手動の固定値。entry数は予算に比例して64〜1024。
  これは画素payloadの予算で、driverが確保する実VRAM全量ではない。
  総画像予算（保持＋256MiB）に一度だけ計上し、同じbitmapのaliasは二重計上しない。
- contextの同一性、generation、既存の完全なcache key（frame・usage・viewport・DPI・変換・描画状態等）を照合する。
  モデル/ファイル依存検証を維持し、borrow前と出力差替え前に再検証する。
- ON/OFF切替・purgeで保持entryを解放。source破棄でも解放。弱参照ownerはsourceを延命しない。
- miss/予算超過はRAM/ディスクへ戻る。書出しは従来経路を維持する。

128MiBには1080p BGRAを約16枚、2048MiBには約260枚を保持できる。保持しきれない長い巡回では通常GPU missとなるため、
小さいworking setでの速度を長いタイムライン全体に適用しない。
自動配分は `GpuMemoryPolicy`（純粋な方針）と `GpuMemoryController`（1秒ごとの採取）。規則は [キャッシュ仕様](CACHE_BEHAVIOR.md) の「メモリと容量」。

## 検証

source-only WARP試験で、同じ8フレームを100回表示するRAM/GPU各3passを順序を変えて比較。
warmupを測定区間の外へ出し、既存の100フレーム巡回ベンチも残す。
GPU hit 100件のtraceにCopyFromMemoryが一度もないことを検証する。
8枚のhost画素一致、表示中画像を保持したままLRU全eviction、予算alias計上、viewport変更、
モデル変更、purge、source破棄後の予算返却を検証する。
従来RAM/ディスク試験はGPU保持を無効にして個別経路の回帰を確認する。

## 根拠

[ID2D1CommandList](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist)
はbitmapを参照で保持する。従ってホストgraphを参照ごと保存するだけでは不変なcacheにならない。
[QueryInterface](https://learn.microsoft.com/en-us/windows/win32/api/unknwn/nf-unknwn-iunknown-queryinterface%28refiid_void%29)
で取得した参照は独立してReleaseする必要がある。

予算36フレームで90フレームを4周するportable試験では、通常LRUのhit=0に対し、
頻度に基づく追加でhit=108。これはポリシーのシミュレーションで、実GPUの速度測定ではない。
