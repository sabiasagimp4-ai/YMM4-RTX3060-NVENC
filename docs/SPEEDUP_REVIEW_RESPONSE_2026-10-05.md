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

## 大きな PSD の計測と改善

[変更前の CI 37283059478](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37283059478) は通常 3 ジョブ成功。各 PSD は生成した 1920 × 1920・7 層 RGBA raw + RGB 合成画像、114,279,164 bytes（約 109 MiB）。立ち絵 2・20 ボイスのタイムラインに、共有設定として eye 200・mouth 100 の定義を置く。15 fps / 120 フレーム（8 秒）の範囲で、WARP・GPU 保持 OFF・RAM 256 MiB。画素検査・初回保存は 2 回目の時間に含めない。

| sample | OFF ms/frame | 2 回目 ms/frame | hit / pixel |
|---|---:|---:|---|
| 1 | 12.90532 | 5.26879 | 120 / 120 全バイト一致 |
| 2 | 6.68299 | 5.22443 | 120 / 120 全バイト一致 |

同じ CI の 309 枚 animation（#13 の出力寿命修正を含む）は、OFF 1.76264 / 1.37758 ms、2 回目 2.02010 / 1.90849 ms、各 900 hit・900 画素一致。

PSD は 2 回目が速いものの確認だけで約 5.2 ms かかるため、次を変更する。

- モジュールの監査結果は AssemblyLoad の世代で使い回す。新しい assembly が載れば一覧を再確認し、collectible の依存 DLL は高速経路に入れない。MVID・所在・重複の確認を維持する。
- 共有設定の変更通知の世代と、毎回の全プロパティの bounded な値の検査で、JSON を使い回す。通知を出さない Offset / Interval / Layers も毎回読む。JSON は検査時に捕えた値だけから作り、変動する実オブジェクトをもう一度読まない。上限、global serializer defaults の影響拒否、native が古い正規化設定を保持する反例は残す。
- 読み込み済み PSD の SHA-256 は、parser が所有する read-only の byte array をコピーせず背景 task に渡す。native のオブジェクトは背景で呼ばない。完了するまでそのフレームは通常描画で、保存・再利用を許可しない。古い読込済みデータと新しいファイルの取り違えは引き続き拒否する。

既存の検査に加え、100 回の通知なしの変更で JSON の全値が従来の fresh serializer と一致すること、未変更時に同じ JSON string を返すこと、A→B→A の変更を正しく扱うことを検査する。改善後の数値は変更 head の CI で確認する。

## 改善後の計測（GPU 設定変更前の head）

[CI 37284876915](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37284876915) / #12 と [CI 37285009887](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37285009887) / #13 は通常 3 ジョブすべて成功。フォルダー時刻の復元、設定の 100 回の値比較と A/B/A、旧読込データの拒否を含む検査も成功した。

| head / fixture | OFF sample 1 / 2 ms | 2 回目 sample 1 / 2 ms | 2 回目 / 同じ sample の OFF |
|---|---:|---:|---:|
| #12・309 PNG | 2.00454 / 1.53192 | 1.49396 / 1.45921 | 0.745 / 0.953 |
| #13・309 PNG | 1.58857 / 1.12604 | 1.36079 / 1.05171 | 0.857 / 0.934 |
| #13・109 MiB PSD + 300 設定 | 5.22014 / 8.14940 | 3.37013 / 3.35161 | 0.646 / 0.411 |

全 sample で animation は 900/900 hit・画素一致、PSD は 120/120 hit・画素一致。GPU hit は 0。PSD の 2 回目は従来の約 5.2 ms から約 3.4 ms になった。ただし変更前後は別の CI job で、OFF もばらつく。PSD の OFF で割った比は変更前 0.408 / 0.782 に対し変更後 0.646 / 0.411 と一方が悪化し一方が改善した。実機やすべての PSD で一定の改善率を保証する結果ではない。生成素材は記載した部品数・サイズ・設定の範囲に限定する。

## GPU 上限と旧既定の移行

[GPU 配分の対応](GPU_BUDGET_REVIEW_2026-10-05.md) は #9 と後続 PR に反映。自動配分は dedicated VRAM の 1/2（OS 予算・余裕・利用者上限も同時適用）。旧設定の自動・2048 MiB だけを一度 -1 へ移し、専用 marker を保存する。手動設定と後から選び直した値は維持する。JSON からの移行・保存・再読込・再選択の検査を追加した。

#13 の SHA task は ExecutionContext の flow を抑え、host の描画・readiness の状態を背景 task に持ち越さない。同期の待ちや host native object の背景呼び出しはしない。GPU とこの変更を含めた head の CI は完了後に PR に記録する。

## GPU 変更を含む実装 head の確認

GPU を含む #9〜#12 の通常 CI はそれぞれ [37285232309](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37285232309)、[37285307481](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37285307481)、[37285319474](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37285319474)、[37286061146](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37286061146) が成功。

#13 の実装 head `6bb7b1dd996f764e62d0a4cfceec471dad179da8` の [CI 37286577932](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37286577932) も通常 3 ジョブ成功。移行の保存・再読込・再選択、既存の GPU 再訪・read-ahead・device lost・資源解放を含めて成功した。

| head / fixture | OFF sample 1 / 2 ms | 2 回目 sample 1 / 2 ms | 2 回目 / 同じ sample の OFF |
|---|---:|---:|---:|
| #12・309 PNG | 2.02081 / 1.57715 | 1.50836 / 1.50743 | 0.746 / 0.956 |
| #13・309 PNG | 1.26092 / 1.09140 | 1.03452 / 0.75189 | 0.820 / 0.689 |
| #13・109 MiB PSD + 300 設定 | 4.19754 / 8.45567 | 2.01451 / 2.07750 | 0.480 / 0.246 |

各 2 回とも animation は 900/900、PSD は 120/120 の hit と全バイト画素一致。GPU hit は 0。異なる job の OFF も変わるため、この追加結果も安定した実機の改善率の証明として扱わない。最終の文書 head の CI と再計測は PR #13 に追記し、文書更新のための再測定を繰り返さない。

## レビューと取り込みの順序

全件 Draft・未マージ、main への書込み・preview リリースは行っていない。#14 → #5 → #6 → #7 → #8 → #9 → #10 → #11 → #12 → #13 の順でレビューする。#14 は main からの独立修正、#5〜#13 は累積の main 宛 PR。#14 の修正は既存 #10 / #13 にも含まれるので、取り込んだ後の次の差分と重複を確認する。

立ち絵の対応範囲は引き続き「一部」。表示区間内の idle、起動間のキャッシュ、多様な実プロジェクト・PSB・実機 GUI / 音声 / Present は今回保証しない。6 件のレビュー対応は、上記の修正・検査・判断理由・今後の課題として記録した。

設定の値のエンコードは、監査済みの ImmutableList と 4 種類の子だけを受け入れる。IEnumerable を実装する外部の派生 eye 型を空の配列と誤認すると native の Offset / Layers をキーから落とすため、明示的に拒否し、動的に作った反例を検査に追加した。
