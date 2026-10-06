# YMM4 の版ごとの対応

このページは `.github/workflows/ymm4-compat.yml` が自動で書き換えます（2026-10-06 更新、プラグイン 0.2.0-preview.4 の後の開発版 b72be26）。手で編集しないでください。

## 確かめ方

1. **ファイルの照合（Linux、全版）**: YMM4 の更新サーバーから、YMM4 自身の更新と同じ手順で各版の DLL と実行環境の設定を取得します。YMM4 が動く .NET の版、プラグインが参照する YMM4 の DLL の版、描画キャッシュが前提とする YMM4 のコードが確かめた版と同じか（HostContracts）を調べます。
2. **起動（Windows、.NET の版が合う版）**: その版の YMM4 にプラグインを入れて実際に起動し、プラグインが読み込まれたか、NVENC 出力の取消保護と描画キャッシュのどの機能を使うと判断したかを、プラグイン自身の報告で記録します。
3. **検査（Windows、新しい版）**: その版に対してプラグインと検査をビルドし、キャッシュのキー検査と実ホスト検査（出力フック、画素一致）を動かします。

CI のランナーには NVIDIA の GPU がないため、NVENC で実際にエンコードできるかは確かめていません。「NVENC 出力」の ○ は、YMM4 の出力に取消保護を接続できたことを表します。

**○** 使える　**△** 一部だけ使える　**×** 使えない　**？** 未確認　**—** 読み込めないため対象外

## 結果

| YMM4 | 読み込み | NVENC 出力 | 描画キャッシュ | 備考 | 確認 |
| --- | :---: | :---: | :---: | --- | --- |
| 4.56.1.0 | ○ | ○ | ○ | コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.56.0.1 | ○ | ○ | △ | キャッシュで使わない機能: シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版（4.56.1.0 と同じ部分も使用） | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.56.0.0 | ○ | ○ | △ | キャッシュで使わない機能: シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版（4.56.1.0 と同じ部分も使用） | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.55.1.1 | ○ | ○ | △ | キャッシュで使わない機能: シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.55.1.0 | ○ | ○ | △ | キャッシュで使わない機能: シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.55.0.1 | ○ | ○ | △ | キャッシュで使わない機能: シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.55.0.0 | ○ | ○ | △ | キャッシュで使わない機能: シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.54.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.54.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.9 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.8 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.7 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.6 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.5 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.4 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.3 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.53.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.8 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.7 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.6 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.5 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.4 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.3 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.52.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.51.0.3 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.51.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.51.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.51.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.50.0.3 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.50.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.50.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.50.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.49.1.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.49.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.49.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.49.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.1.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.1.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.1.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.0.5 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.0.4 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.0.3 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.48.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.47.0.5 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.47.0.4 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.47.0.3 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.47.0.2 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.47.0.1 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.47.0.0 | ○ | ○ | △ | キャッシュで使わない機能: 選択枠、ランダム系の効果、シンプル立ち絵、口パクの完成判定、動く立ち絵、PSD 立ち絵、FFmpeg の動画。コードを読んだ版 | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合・起動（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.7 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.6 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.5 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.46.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.7 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.6 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.5 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.4.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.3.5 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.3.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.3.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.3.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.3.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.3.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.2.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.2.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.45.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.44.0.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.44.0.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.44.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.44.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.44.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.6 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.5 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.43.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.1.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.1.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.0.5 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.0.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.0.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.42.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.6 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.5 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.41.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.40.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.40.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.39.0.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.39.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.39.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.39.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.38.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.38.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.37.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.37.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.37.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.2.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.2.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.2.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.0.4 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.0.3 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.0.2 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.36.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.35.3.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.35.2.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.35.1.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.35.1.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.35.0.1 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.35.0.0 | × | — | — | YMM4 が .NET 9 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.34.2.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.34.1.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.34.1.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.34.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.34.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.33.1.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.33.0.3 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.33.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.33.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.33.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.32.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.32.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.32.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.31.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.30.1.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.30.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.30.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.29.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.29.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.29.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.2.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.2.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.1.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.1.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.0.3 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.28.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.27.2.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.27.1.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.27.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.27.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.26.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.26.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.26.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.25.0.4 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.25.0.3 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.25.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.25.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.25.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.6 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.5 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.4 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.3 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.24.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.23.0.2 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.23.0.1 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |
| 4.23.0.0 | × | — | — | YMM4 が .NET 8 で動くため、.NET 10 向けのプラグインを読み込めません | 2026-10-06、0.2.0-preview.4 の後の開発版 b72be26、照合（[記録](https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198)） |

## 実行記録

- https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37425871198
