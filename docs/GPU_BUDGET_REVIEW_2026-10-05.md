# GPU 自動配分のレビュー対応

Claude レビュー §6 に従い、自動配分の物理上限を搭載 dedicated VRAM の 1/2 とする。OS の予算、YMM4 自身の使用量、max(1 GiB, OS 予算の 20%) の余裕、利用者の上限、最大 8 GiB も同時に適用する。12 GB / 8 GB / 4 GB の合成 sample はそれぞれ 6 GB / 4 GB / 2 GB を越えない。測定できた integrated の少量 dedicated VRAM も 1/2 に従う。測定できない WARP は従来の 128 MiB のまま広げない。手動の固定設定は利用者の選択として維持する。

旧設定の AutomaticGpuBudget=true / GpuLimitMiB=2048 だけは、初回 Initialize で -1（自動の上限）へ移す。GpuBudgetMigrationVersion=1 を保存し、後から自動・2048 を選んだ場合は再移行しない。手動・2048、手動・0、自動の他の上限は変更しない。既存のプレビュー・出力の switch の移行は別の SettingsVersion として維持する。

YMM4 の SettingsBase は読込時に Initialize するが、自動で Save しないため、PluginSettings.Apply は移行済み設定の初回保存を行う。以後は通常の変更時だけ保存する。

検査は VRAM の 4 / 8 / 12 GB、少量・0 の物理上限、OS の余裕、圧迫時の縮小、回復の連続 sample、利用者の上限を確認する。real host の検査は 5 種類の設定を JSON から読み、移行、保存された marker の再読込、後から選んだ 2048 の維持を確かめる。既存検査は残す。

ローカルの StoreChecks と HostCacheProbe / 製品ビルドは成功（警告・エラー 0）。Windows CI の結果は PR の最終 head と合わせて記録する。実機の OS 予算・急な出力負荷での調整は利用者 PC の trace で引き続き確認する。
