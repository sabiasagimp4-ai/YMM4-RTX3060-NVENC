# YMM4 RTX 3060 NVENC・描画キャッシュ

YukkuriMovieMaker4の動画出力・描画キャッシュプラグインです。NVENCによるMP4出力と、YMM4標準プレビュー／動画出力のフレーム再利用を個別に使えます。開発の基準は `main` です。

**開発中です。** 基準ホストはYMM4 Lite 4.56.1.0。Windows CIでビルド・実ホスト・画素一致・GPU保持を検証し、別のCIでは実YMM4 GUIを操作してログを採取しています。AEに近い挙動を目標にしており、再生前キャッシュ、音声と同期したレンダー待機、MFRは未実装です。

## ビルドとインストール

Windows、.NET 10 SDK、Visual Studio 2022のC++ツール、Windows SDKが必要です。NVENC出力には対応するNVIDIA GPUとドライバーが必要です。RTX 3060ではH.264／HEVCを使います（AV1エンコード非対応）。

使用するYMM4のフォルダーを明示してビルドします。新しいYMM4のDLLでビルドしたプラグインを古いYMM4へ入れると、参照アセンブリを読み込めない場合があります。ビルド時に対象ホストでの型ロードも検査します。

```powershell
.\build.ps1 -Ymm4DirPath 'D:\YukkuriMovieMaker_v4_Lite' -Smoke
```

`-Smoke` はNVENCのH.264／HEVC＋AAC出力、デコード、取消・異常入力等を検査します。NVIDIA GPUとPATH上の `ffmpeg`／`ffprobe` が必要です。通常のビルドでは省略できます。成功すると `dist/YMM4-RTX3060-NVENC.ymme` とSHA-256ファイルを生成します。Smokeを指定した場合は検査成功後に生成し、YMM4へ自動配置はしません。

1. 作業中のプロジェクトを保存してYMM4を終了します。
2. `.ymme` を開き、使用するYMM4へインストールします。
3. YMM4を起動し、短い区間で表示・映像・音声・同期を確認します。

## 設定と使い方

ツール「描画キャッシュ」と「その他 > RTX 3060 NVENC・描画キャッシュ」で同じ設定を変更できます。

| 設定 | 対象 | 新規インストール時 |
| --- | --- | --- |
| プレビューで描画キャッシュを使う | 標準プレビュー、停止中の先読み、キャッシュバー | OFF |
| 動画出力で描画キャッシュを使う | YMM4標準形式とNVENCの描画 | OFF |
| RTX 3060 NVENC出力を使う | 出力形式「RTX 3060 NVENC 出力」 | ON |

プレビューの完成フレームを保存・再利用します。緑の帯はRAM、青はディスク。RAMは空きメモリに応じて調整し、設定上限の既定は2048 MiBです。停止中は操作から既定8秒後にタイムライン全体を先読みします。GPU上に保持できる反復フレームでは、RAMからの再転送を省きます。

詳細ログはツールから開始／停止できます。既定OFFで、実行時に読み込まれた処理を動的に観測します。未知の処理を自動的にキャッシュ対象へ変更する機能ではありません。未確認の外部処理を使う区間は通常描画となり、信頼する外部プラグインは個別に設定できます。

詳しい保存条件、上限、選択枠、素材・フォント・乱数の扱いは [キャッシュ仕様](docs/CACHE_BEHAVIOR.md) を参照してください。

## NVENC出力

解像度・fpsに応じた可変ビットレートが既定です。幅・高さは偶数が必要です。出力設定（コーデック・ビットレート方式・品質など）はプラグインの設定に保存され、次回の起動でも使います。取り消した・失敗した出力の途中ファイルは削除します。GPUフレームを所有D3D11テクスチャへコピーし、CPU readbackを介さずNVENCへ渡します。キャッシュ保存のreadbackとは別の経路です。

入力・出力キューに上限を設け、NVENC呼出しは専用MTAスレッドで実行します。先行音声は一時ファイルへ退避し、AACは44.1／48 kHzのモノラル／ステレオに対応します。MP4のサンプル索引は出力時間に比例してRAMを使います。

取消・失敗・予定フレーム不足では既存の完成ファイルを置き換えず、途中の `.partial` を削除します。デバッグ有効時の `.partial.nvenc_log.txt` は出力先に残ります。OSやドライバー内部の停止を強制解除する仕組みではありません。

## 開発・検証資料

- [開発状況・次の課題](docs/AE_CACHE_DEVELOPMENT.md)
- [AE SDKと現行キャッシュの契約差](docs/AE_CACHE_CONTRACTS.md)
- [ホスト契約と更新手順](docs/HOST_CONTRACTS.md)
- [詳細ログの採取と集計](docs/CACHE_DIAGNOSTICS.md)
- [GPU保持の設計](docs/GPU_FRAME_RETENTION.md)
- [処理ログの実測](docs/CACHE_TRACE_RESULTS_2026-10-01.md)・[GPU保持の実測](docs/GPU_FRAME_RETENTION_RESULTS_2026-10-01.md)
- [30秒・421アイテムの実YMM4負荷試験](docs/STRESS_GUI_RESULTS_2026-10-02.md)
- [性能調査](docs/PERFORMANCE_REVIEW_2026-10-02.md)・[修正前後の測定](docs/PERFORMANCE_RESULTS_2026-10-02.md)
- [実ホスト検証の実行方法](tests/HostCacheProbe/README.md)

統合時の [main CI](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/36878854622) は成功しました。WARPの8枚反復ではRAM復元平均8.264 ms／枚、GPU再利用1.731 ms／枚でした。実プロジェクト全体やRTX 3060の速度倍率ではありません。過去のRTX 3060上のオフラインNVENC試験と、現在のWARPキャッシュ試験も区別して扱います。

## 由来とライセンス

MITの [YMM4_NVEncPlugin](https://github.com/tarutaru247/YMM4_NVEncPlugin) を基にしています。[Radeon AMF実装](https://github.com/disnana/YMM4_AMF_Plugin) と [GPU出力の解析記事](https://qiita.com/harupython/items/f03cd6f04375115f82f9)、[高速化の記事](https://qiita.com/harupython/items/4be768e58cba3a2921b3) を調査の参考にしました。AMFとlibvipsは組み込んでいません。

本体は [MIT](LICENSE)。元実装・Harmony・NVIDIAヘッダーの由来は [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を参照してください。YMM4本体やNVIDIAドライバーは同梱しません。

動的な計算共有・依存報告・費用判断・無損失圧縮の契約は [DYNAMIC_CACHE_API.md](docs/DYNAMIC_CACHE_API.md) を参照。
