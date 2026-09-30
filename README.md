# YMM4 RTX 3060 NVENC 出力

RTX 3060 を搭載したこの PC 向けの、ゆっくりMovieMaker4 動画出力プラグインです。YMM4 の描画済み GPU フレームを Direct3D 11 経由で NVENC に渡し、MP4（H.264 または HEVC、AAC 音声）を出力します。編集画面や素材の読み込みを速くする機能ではありません。

MIT ライセンスの [YMM4_NVEncPlugin](https://github.com/tarutaru247/YMM4_NVEncPlugin) を基にしています。参考にした [Radeon AMF 実装](https://github.com/disnana/YMM4_AMF_Plugin) と [Qiita の解析記事](https://qiita.com/harupython/items/f03cd6f04375115f82f9) は GPU フレームの受け渡しと出力パイプラインの設計に役立ちました。Radeon 用 AMF や素材キャッシュは組み込んでいません。

## この PC での対象

- Windows 11、YMM4 Lite v4.55.1.1（.NET 10）、i7-13700KF、RTX 3060 12 GiB、NVIDIA ドライバー 610.62。
- H.264 と HEVC の NVENC 出力。RTX 3060 は AV1 エンコードに対応しないため、AV1 は設定から除外しています。
- 既定値は H.264、標準品質、解像度と fps に応じた可変ビットレートです。品質やビットレートを変えると画質と速度も変わります。
- GPU フレームは CPU に読み戻さず、所有 D3D11 テクスチャへコピーして NVENC に入力します。入力テクスチャの寿命を越えて参照しない設計です。

## ビルドとインストール

この PC には .NET 10 SDK、Visual Studio 2022 の C++ ツール、Windows SDK 10.0.26100.0 が必要です。YMM4 の配置を変えた場合は `-Ymm4DirPath` を指定してください。

```powershell
.\build.ps1 -Smoke
```

成功すると `dist/YMM4-RTX3060-NVENC.ymme` と SHA-256 ファイルができます。`-Smoke` はこの GPU で H.264/HEVC と AAC の1秒の MP4 を実際に生成・デコードします（`ffmpeg` と `ffprobe` が PATH に必要）。通常のビルドでは `-Smoke` を省けます。ビルドは YMM4 のプラグインフォルダーへ自動配置しません。

1. YMM4 の作業中のプロジェクトを保存して終了します。
2. `.ymme` を開いて、インストーラーで普段使う YMM4 を選びます。
3. YMM4 を起動し、動画出力の「RTX 3060 NVENC 出力」を選びます。まず短い区間を新しいファイル名で出力し、映像・音声・同期を確認してください。

偶数幅・偶数高さが必要です。失敗時は完成ファイルを置き換えず、出力先に `.partial` ファイルを残します。デバッグログを有効にした場合は、同じ出力先に `.partial.nvenc_log.txt` が残ります。不要になった失敗ファイルは内容を確認してから削除してください。

## 検証範囲

この PC のドライバー上で H.264/HEVC の NVENC と AAC の 30 フレーム MP4 をオフラインで生成し、`ffprobe` と全フレームデコードで確認しました。YMM4 の旧形式フレームが届いた場合や映像なしの音声が届いた場合も、無言で成功扱いせず失敗することを確認しました。YMM4 の実プロジェクトでの速度比較、長時間出力、キャンセル時の動作は未検証です。描画が主なボトルネックの場合、NVENC を使っても出力速度が大きく変わらないことがあります。

## ライセンス

本体は [MIT](LICENSE) です。元実装と NVIDIA ヘッダーの由来は [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を参照してください。YMM4 本体や NVIDIA ドライバーは同梱しません。
