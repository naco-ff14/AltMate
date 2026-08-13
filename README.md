# AltMate

同じPCで起動している複数のFFXIVキャラクターを支援するDalamudプラグインです。

## 主な機能

- リーダーへの追従・相乗り・戦闘・エリア移動の連携
- 複数キャラクターのハウジング抽選・空き土地管理
- Penumbraで差し替えたエモートの一覧表示と再生
- キャラクター・リテイナー・FCチェストのギル集計
- 別Dalamud環境間でのローカル設定共有

## インストール

Dalamud設定の「カスタムプラグインリポジトリ」に次のURLを追加します。

`https://raw.githubusercontent.com/naco-ff14/AltMate/main/repo.json`

## 任意連携

- Lifestream
- Penumbra
- BossMod Reborn / Rotation Solver Reborn

## データについて

共有データは同じPC内の`%LOCALAPPDATA%\AltMate\shared-config.json`へ保存されます。外部サービスへキャラクターデータを送信しません。

## ビルド

Dalamud API 15 / .NET 10。`dotnet build -c Release`でビルドできます。
