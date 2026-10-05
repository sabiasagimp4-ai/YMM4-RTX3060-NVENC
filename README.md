<h1 align="center">YMM4 NVENC・描画キャッシュ</h1>

<p align="center">
ゆっくりMovieMaker4 の動画出力を NVIDIA NVENC で。<br>
プレビューと動画出力で描き終えたフレームを RAM・ディスク・GPU に取っておき、同じフレームは描き直さずに出します。
</p>

<p align="center">
  <a href="https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/releases"><img src="docs/assets/readme/badge-release.svg" alt="release"></a>
  <img src="docs/assets/readme/badge-dotnet.svg" alt=".NET 10">
  <a href="LICENSE"><img src="docs/assets/readme/badge-license.svg" alt="license: MIT"></a>
</p>

> [!CAUTION]
> **開発中のプレビュー版です。** 入れる前に作業中のプロジェクトを保存してください。配布しているのは、このリポジトリの [Releases](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/releases) だけです。

> [!IMPORTANT]
> 描画キャッシュは、新しく入れたときは **OFF** です。ツール「描画キャッシュ」で ON にしてください。NVENC 出力には NVIDIA の GPU が必要です。


## できること

| 機能 | 内容 |
| --- | --- |
| **NVENC 出力** | H.264・HEVC・AV1（AV1 は RTX 40 シリーズ以降）。YMM4 が描いた GPU のフレームを、CPU を通さずにエンコーダーへ渡します |
| **描画キャッシュ** | 標準プレビューと動画出力で、描き終えたフレームを保存して使い回します。ツールの帯は緑が RAM、青がディスク |
| **停止中の先読み** | 手を止めて 8 秒たつと、まだ描いていないフレームを裏で描いておきます。ランダム移動などのランダム系も描きます。動く立ち絵・PSD 立ち絵が映る区間は、再生したときに保存します |
| **編集に強い** | キーはフレームごと。編集したアイテムの区間だけが描き直しになり、Undo で戻すと保存済みのフレームが戻ります |
| **YMM4 と同じ絵** | 完成を確かめられないフレーム（読み込み途中の動画など）は保存しません。中身を確かめられない外部プラグインの区間は、YMM4 がそのまま描きます |

## はじめる

1. YMM4 を終了します。
2. [Releases](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/releases) から `YMM4-RTX3060-NVENC.ymme` をダウンロードして開き、YMM4 へインストールします（SHA-256 のファイルも添付しています）。
3. YMM4 を起動し、ツール「描画キャッシュ」でキャッシュを ON にします。短い区間で表示・映像・音声・同期を確かめてから使ってください。

## 対応 GPU

NVENC 出力には、NVENC を積んだ NVIDIA GPU とドライバー（NVENC API 13.0、R570 以降）が必要です。描画キャッシュは GPU の種類を問いません。

| GPU | H.264 | H.265（HEVC） | AV1 |
| --- | --- | --- | --- |
| GeForce RTX 20／30 シリーズ、GTX 16 シリーズなど | ○ | ○ | × |
| GeForce RTX 40／50 シリーズ | ○ | ○ | ○ |

GPU の種類による決め打ちはありません。出力のたびに、その GPU の NVENC が対応するコーデックと最大解像度を問い合わせます。対応しない組み合わせや古いドライバーでは、理由と対処を日本語で表示します。非同期エンコードに対応しない GPU では同期処理に切り替えます。

NVENC は、YMM4 が描画に使う GPU と同じ GPU で動きます。内蔵 GPU で描画しているノート PC では、Windows のグラフィックス設定で YukkuriMovieMaker を「高パフォーマンス」にしてください。

実機で確認したのは RTX 3060（H.264／HEVC）だけです。AV1 と他の GPU は未確認です。

## 設定

ツール「描画キャッシュ」と「その他 > NVENC・描画キャッシュ」で同じ設定を変更できます。

| 設定 | 対象 | 新規インストール時 |
| --- | --- | --- |
| プレビューで描画キャッシュを使う | 標準プレビュー、停止中の先読み、キャッシュバー | OFF |
| 動画出力で描画キャッシュを使う | YMM4 標準形式と NVENC の描画 | OFF |
| NVIDIA NVENC 出力を使う | 出力形式「NVIDIA NVENC 出力」 | ON |

停止中の先読みは、ツール「描画キャッシュ」を開いている間、操作から既定 8 秒後にタイムライン全体を巡回します。範囲と順番、同時に描く描画器の数（Auto・1・2・4 本。Auto はコア数と VRAM から決めます）は変えられます。

| メモリ | 自動配分（既定） | 上限の既定 |
| --- | --- | --- |
| RAM | 空き物理メモリに応じて調整 | 2048 MiB |
| VRAM（GPU 保持） | 描画に使う GPU の VRAM の予算と使用量に応じて調整 | Auto（最大 8192 MiB） |

GPU 保持では、表示したフレームを GPU 上に残し、RAM からの再転送を省きます。

- VRAM の自動配分は、搭載 VRAM の 1/2 まで（最大 8 GiB）、かつ上限までです。YMM4 自身と他のアプリの分を残します。専用の VRAM がない内蔵 GPU では GPU 保持を使いません（RAM からの表示は使えます）。
- 前の版の既定（自動・2048 MiB）のままの設定は、一度だけ Auto に移します。
- 他のアプリが VRAM を使って逼迫すると、すぐに減らします。
- 手動にすると、上限の値で固定します。「使わない」も選べます。

詳細ログはツールから開始／停止できます（既定 OFF）。

## 自動で対象になるもの・ならないもの

画像・動画・テキスト・字幕・連番画像・フォント（代替フォントを含む）、同梱の Community プラグインのうちコードを読んで確かめた約 70 種は、自動でキャッシュの対象です。

YMM4 4.56.1.0 同梱の立ち絵（シンプル立ち絵、動く立ち絵の PNG の部品、PSD 立ち絵）も対象にします。口パクは YMM4 の音量の計算の完了を確かめてから保存します。動く立ち絵と PSD 立ち絵はまばたきが起動ごとに変わるため、そのフレームはその起動の間だけ使います。

| 対象にならないもの | 理由 |
| --- | --- |
| 中身を確かめていない外部プラグイン | 中で何を読むか分からない。設定で個別に「信頼」すれば対象にできます |
| OpenFX・VST3 | 外部のネイティブのプログラム |
| 残像・モーションブラー・CircularBlur | 直前に描いたフレームによって絵が変わる |
| 立ち絵の一部 | 同梱版以外、グループ・入れ子の中、動く立ち絵の PNG 以外の部品など。完成を確かめる方法をまだ用意していない（[調査](docs/LIPSYNC_RESEARCH_2026-10-04.md)） |
| 同じレイヤーで時間が重なるアイテム | YMM4 はアイテム一覧の順で重ねるが、その順をキーに入れていない |

対象にならないものが映る区間だけ、YMM4 がそのまま描きます（ほかの区間は対象のまま）。一覧と理由は [自動対象の範囲](docs/AUTOMATIC_COVERAGE_2026-10-03.md)、外部プラグインを自動で判定する案は [外部プラグインの判定](docs/EXTERNAL_PLUGINS_2026-10-04.md)、保存条件・上限・乱数の扱いは [キャッシュ仕様](docs/CACHE_BEHAVIOR.md) を参照してください。

## NVENC 出力

解像度・fps に応じた可変ビットレートが既定です。幅・高さは偶数が必要です。出力設定（コーデック・ビットレート方式・品質など）はプラグインの設定に保存され、次回の起動でも使います。GPU のフレームを所有 D3D11 テクスチャへコピーし、CPU readback を介さず NVENC へ渡します。キャッシュ保存の readback とは別の経路です。

入力・出力キューに上限を設け、NVENC 呼出しは専用 MTA スレッドで実行します。先行音声は一時ファイルへ退避し、AAC は 44.1／48 kHz のモノラル／ステレオに対応します。MP4 のサンプル索引は出力時間に比例して RAM を使います。

取消・失敗・予定フレーム不足では既存の完成ファイルを置き換えず、途中の `.partial` を削除します。デバッグ有効時の `.partial.nvenc_log.txt` は出力先に残ります。OS やドライバー内部の停止を強制解除する仕組みではありません。

## 自分でビルドする

Windows、.NET 10 SDK、Visual Studio 2022 の C++ ツール、Windows SDK が必要です。使用する YMM4 のフォルダーを明示してビルドします。新しい YMM4 の DLL でビルドしたプラグインを古い YMM4 へ入れると、参照アセンブリを読み込めない場合があります。ビルド時に対象ホストでの型ロードも検査します。

```powershell
.\build.ps1 -Ymm4DirPath 'D:\YukkuriMovieMaker_v4_Lite' -Smoke
```

`-Smoke` は NVENC の H.264／HEVC＋AAC 出力、デコード、取消・異常入力等を検査します。NVIDIA GPU と PATH 上の `ffmpeg`／`ffprobe` が必要です。通常のビルドでは省略できます。成功すると `dist/YMM4-RTX3060-NVENC.ymme` と SHA-256 ファイルを生成します。YMM4 へ自動配置はしません。

README の画像（バナー・バッジ・カード）は `python tools/readme-assets.py` で描き直せます（リリースのバッジはプロジェクトの版から読みます）。

## 開発・検証資料

開発の基準は `main` です。Windows CI でビルド・実ホスト・画素一致・GPU 保持を検証し、別の CI では実 YMM4 の GUI を操作してログを採取しています。AE に近い挙動を目標にしており、再生前キャッシュ、音声と同期したレンダー待機、MFR は未実装です。

- [開発状況・次の課題](docs/AE_CACHE_DEVELOPMENT.md)
- [目的・invariantsからのアーキテクチャ監査と改善](docs/CACHE_ARCHITECTURE_AUDIT_2026-10-04.md)
- [AE SDK と現行キャッシュの契約差](docs/AE_CACHE_CONTRACTS.md)
- [ホスト契約と更新手順](docs/HOST_CONTRACTS.md)
- [詳細ログの採取と集計](docs/CACHE_DIAGNOSTICS.md)
- [GPU 保持の設計](docs/GPU_FRAME_RETENTION.md)
- [処理ログの実測](docs/CACHE_TRACE_RESULTS_2026-10-01.md)・[GPU 保持の実測](docs/GPU_FRAME_RETENTION_RESULTS_2026-10-01.md)
- [30 秒・421 アイテムの実 YMM4 負荷試験](docs/STRESS_GUI_RESULTS_2026-10-02.md)
- [性能調査](docs/PERFORMANCE_REVIEW_2026-10-02.md)・[修正前後の測定](docs/PERFORMANCE_RESULTS_2026-10-02.md)
- [動的な計算共有・依存報告・費用判断・無損失圧縮の契約](docs/DYNAMIC_CACHE_API.md)
- [実ホスト検証の実行方法](tests/HostCacheProbe/README.md)

## 由来とライセンス

MIT の [YMM4_NVEncPlugin](https://github.com/tarutaru247/YMM4_NVEncPlugin) を基にしています。[Radeon AMF 実装](https://github.com/disnana/YMM4_AMF_Plugin) と [GPU 出力の解析記事](https://qiita.com/harupython/items/f03cd6f04375115f82f9)、[高速化の記事](https://qiita.com/harupython/items/4be768e58cba3a2921b3) を調査の参考にしました。AMF と libvips は組み込んでいません。

本体は [MIT](LICENSE)。元実装・Harmony・NVIDIA ヘッダーの由来は [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を参照してください。YMM4 本体や NVIDIA ドライバーは同梱しません。
