# Claude レビューへの対応（2026-10-05）

対象: `claude/ymm4-nvenc-perf-analysis-5h1aik` の `docs/SPEEDUP_REVIEW_CLAUDE_2026-10-05.md`。main は変更せず、全 PR を Draft・未マージのまま維持する。

## 独立した修正

main から `codex/fix-host-output-and-idle-model` / [PR #14](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/pull/14) を作成。出力寿命とランダム要素の同一性比較を分離し、修正を外すと失敗する mutation control を付けた。main の記述は Format=2、高速化版は Format=3 であり、両方の監査済み形式を扱う。フレームキー比較は保持する。公開・preview.3 作成は今回行わない。

## CI の固定 commit 計測

#5 から #13 に、`workflow_dispatch` の boolean 入力 `measure_baselines`（既定 false）を反映。固定 commit を `git fetch` / worktree する計測手順だけを `github.event_name == 'workflow_dispatch' && inputs.measure_baselines` で制御する。push / PR / 通常の手動実行では過去 commit を取得しない。host・pixel・遅延 envelope・更新・キャンセルなどの検査は常時実行する。portable の workflow 検査は、固定 SHA の取得が無条件に戻る変更を検出する。

## 立ち絵のないフレームの先読み

ルート全体の立ち絵有無ではなく、フレームが TachieItem の `[Frame, Frame + Length)` に重なるかを判定する。立ち絵アイテムの表示区間外は従来どおり複製から先読みする。口パクによる非表示（IsHiddenWhenNoSpeech）を調べるために新しい音量計算を始めることは避け、表示区間内は保守的に見送る。

複製比較ではルート Resources の `animation-blink-session://` / `psd-blink-session://` の値だけも伏せる。接頭辞・件数・他の描画記述・入れ子の資源は保持する。フレームキーの比較は引き続き完全一致を要求する。実際の検査は非表示区間にある立ち絵と表示中の図形を含むシーンで 6 フレームの先読み、RAM hit、全バイトの画素一致、表示区間の見送りを確認する。

### 計算枠が空いた時だけ描く案を今回採用しない理由

監査済み YMM4 4.56 の TachieSource は共有 `SemaphoreSlim(2,2)` の `envelopeCalculationGate` を持ち、非同期タスク中で前世代の終了待ち後に `WaitAsync` し、finally で Release する。`CurrentCount` を観測しても、その後 native Update が予約するまでに再生側が予約できる。こちらが枠を先に取ると、native タスクは同じ semaphore をもう一度待つため、二重取得になり実行できない。予約を渡す API / 再生優先の try-start API はない。

安全に行うには host のスケジューラとの予約受け渡し、計算タスクの開始・待機・取消への新しい介入と監査が必要。空き数を見ただけで再生の枠を残せると判断しない。今回は表示区間だけ見送り、通常の再生での保存・2 回目の再利用を維持する。

## 今後の課題（今回製品の変更は行わない）

- 起動を越える立ち絵の再利用: host と同じまばたき計算を監査し、起動ごとのセッション値の代わりに実際の目部品番号をフレームキーに入れる。現在のディスク記録は次回起動で使えず、容量上限の通常 eviction により除去される。
- Simple tachie: 数千ボイス / 多数の表情境界を持つ長いプロジェクトの記述時間を測る。TryRanges の費用は境界数とアイテム数に依存する。
- 素材確認: `NtQueryInformationByName(FileStatInformation)`（Windows 10 1709 以降）による同期の識別子・更新時刻・サイズ取得を調査する。現在の厳密な同期確認を維持し、通知だけで安全としない。
- 差分記述: 製品は全文方式を維持。1,000 回の actual host 編集と全文の一致検査を残す。
- HEVC hvcC: レビューで規格適合を確認された SPS 由来の値・並び・不正入力拒否を維持。実機 NVENC / GUI の検証は、CI の WARP 成功とは別に利用者 PC で行う。

## 大きな部品フォルダーの計測と改善

変更前の [CI 37282914279](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37282914279) は通常 3 ジョブ成功。309 枚の生成 PNG があるフォルダー、15 fps / 60 秒 / 900 フレーム、立ち絵 2・ボイス 20、WARP・GPU 保持 OFF・RAM 256 MiB で測定した。実際に使う 9 枚以外に 300 枚の部品を置き、フォルダー一覧取得の費用を再現する。画素検査は時間測定の外。

| sample | OFF ms/frame | 2 回目 ms/frame | hit / pixel |
|---|---:|---:|---|
| 1 | 1.98249 | 3.88253 | 900 / 900 全バイト一致 |
| 2 | 1.54031 | 2.84102 | 900 / 900 全バイト一致 |

重かったため、SafeSource のフォルダー全件取得・全パスの正規化を、native の各部品が参照できる `stem*` の同期取得に変更する。同じパスの検証は一度の SafeSource 内で共有する。部品数の上限、INI 拒否、一覧変更後の永続的な見送り、実際の native 設定・部品数の一致は残す。

フォルダーの更新時刻だけでは一覧の使い回しを許可しない。時刻を戻す反例でも新しい番号付き部品を同期で検出する検査を追加した。改善後の実測は変更 head の CI で確認する。
