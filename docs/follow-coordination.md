# AltMate v1.62.0 連携操作改善

対象: naco-ff14/AltMate、基点 `95f21c739982908dd3e0133238c6466d5bd1a8e6` (v1.61.4)。

## 設計比較

FrenRider参照コミット: `70cb51483f1103a71539514fe627d6030f4fb0fd`。
参照: https://github.com/McVaxius/FrenRider/tree/70cb51483f1103a71539514fe627d6030f4fb0fd/FrenRider
指定された11ファイルの責務・関連処理を確認し、AltMate既存実装との差を比較した。ソースの直接コピーは行っていない。

|参照ファイル|確認した設計|AltMateへの適用|
|---|---|---|
|FollowService.cs|移動Backend切替、移動量と経過時間による停滞検出、飛行時の経路再要求|既存SmoothFollowを維持し、BMR優先設定、停滞・距離拡大の検出、上限付きvnav復旧を追加|
|FrenTeleportService.cs / LocalAethernetFollowDetector.cs|位置・エリアの変化と移動待機を分離。サンプルの鮮度・対象を確認|既存キャラ間通信の位置変化を保持。転送先IDが後着しても12秒以内で関連付け、IPC受付拒否を再試行|
|LifestreamAethernetCatalog.cs|通常・Custom・住宅街の転送網を区別。内部データへの依存を隔離|AltMateの既存ID通信、Lifestream公開IPC、ゲームのAethernetGroupを使用。内部カタログのreflectionは導入しない|
|MountService.cs / FrenRiderMountPolicy.cs|相乗り・自前マウント・離陸・着地と所有状態を分離|相乗りの成否に依存しない6秒の切替期限、離陸3回上限、飛行経路、地上復帰後の降車を追加|
|CombatService.cs / AutorotIpcService.cs|戦闘と移動の役割分離、外部プラグインの稼働状態を確認|戦闘BMRから20m超で離れた場合だけ停滞復旧。詠唱・近距離のAI位置取りを保護。Wrath稼働中はRSRを開始しない|
|ExternalAutomationCleanupService.cs|外部操作の開始・解除を記録、終了時のクリーンアップ|AltMateが開始したBMR・RSR・経路だけを停止。緊急停止・通信消失・ログアウト・設定変更で解除|
|ConfigManager.cs / CharacterConfig.cs|共通設定とキャラクター設定を分離、対象ごとの設定解決|追従4項目にGlobal → Character → A-B Pairの上書き設定とUI、キーごとの差分保存を追加|

Wrath状態確認の契約: https://github.com/PunishXIV/WrathCombo/blob/main/docs/IPC.md (`WrathCombo.GetAutoRotationState`)。
vnavmeshの契約: https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/IPCProvider.cs 。
`SimpleMove`の計算完了後に移動を開始する実装を確認したため、`Nav.PathfindCancelable` と `Path.MoveTo` に分離。停止時はAltMate自身のCancellationTokenを取り消し、破棄した計算結果を適用しない。全利用者の経路を取り消すIPCは呼ばない。

## 実装内容

1. **Aethernet追従**: 位置ジャンプの証拠をパケット間・フレーム間で保持。目的地の後着・受付拒否・移動元への接近を扱い、期限切れで破棄。同じ移動元IDへの再要求や通常都市の異なる転送網を拒否。既存の住宅街・Custom・クレセント処理を保持。
2. **Recovery**: 500ms以上の観測窓、2秒の停滞、15m以上で2.5秒の距離拡大を検出。停止・転送時に観測をリセット。
3. **Backend**: 通常追従のBMR優先は新しい任意設定（初期値OFF、従来のSmoothFollowを維持）。停滞時はBMR停止後にvnavへ。到達・終了・15秒経過で解除し、3秒の再試行間隔を設けて通常Backendへ戻る。戦闘BMRは20m超で分離した場合のみ同様の復旧対象。
4. **操作権**: Smooth/BMR/vnavの切替時に旧処理を解除。既存の他者vnav移動には譲る。Wrathを読み取り専用で確認し、ONまたはIPC不明時はRSRの起動を抑止。RSR停止はAltMateが開始したセッションに限定。Lifestream受付直後にも追従を停止。
5. **マウント**: 相乗りできない・距離が離れている状態が6秒続けば自前マウントへ。相乗り中は移動入力を停止。リーダーの飛行状態を通信し、離陸は1.5秒間隔で最大3回。飛行経路を使い、AltMateが要求した自前マウントは着地後に降車を同期。
6. **時間・距離ベース**: 復旧タイマーは単調増加時計を使用。追従目標は実際の経過時間による速度平滑化と120ms先読み。テレポ相当の異常速度は外挿しない。
7. **状態**: Idle / Following / CatchUp / Recovery / AwaitingLeader / Aethernet / Teleport / TerritoryTransition / DutyTransition / Suspended / Mounted / Flying。UIに現在の状態を表示。消失時は転送・通信・再表示待ちとし、古い移動を継続しない。
8. **設定**: 距離・BMR優先・vnav復旧・自前マウント切替の4項目にキャラ/ペアの上書きを追加。ペアキーは follower ContentId : leader ContentId。上書き未設定項目は継承。別キーの同時編集と削除を回帰テストで確認。

## 変更ファイル

- `CharacterLinkCoordinator.cs`: 既存通信・転送・戦闘・マウント処理との接続、解除処理。
- `CharacterLinkCoordinator.Follow.cs`: BMR起動/解除、戦闘復旧、Wrath稼働確認、経路計算完了の適用。
- `FollowStateMachine.cs`: 追従状態、停滞/距離拡大判定、Aethernet観測の保持。
- `FollowController.cs`: 経過時間に基づく速度平滑化・先読み。
- `Configuration.cs`: Global/Character/Pairの設定解決。
- `SharedConfigurationStore.cs`: 新設定の保存・別キーの差分マージ。
- `MainWindow.cs` / `MainWindow.Follow.cs`: 状態・Backend設定・キャラ/ペア別設定UI。
- `tests/Regression/Program.cs` / `FollowRegression.cs` / `Regression.csproj`: 既存テストへの追加。
- `docs/follow-coordination.md`: この説明。

## 検証結果

- `dotnet build AltMate.csproj -c Release --no-restore -v:q`: 成功、0 errors / 29 warnings。
- 警告は既存の旧API利用とNoireLibのSystem.Text.Json警告。同系統の新しいJSON差分処理にも警告が付く。
- `dotnet run --project tests/Regression/Regression.csproj -c Release --no-restore`: 追加・既存テスト成功。
- 追加検証: 30/60fpsで停滞検出時刻差34ms以内、先読み目標差0.01m未満、順調な移動で誤復旧しない、距離拡大、停止と長時間中断、状態の優先順位、転送IDの後着・証拠の期限切れ・消費、テレポ外挿抑止、設定継承、複数クライアント保存・削除。
- `git diff --check`: 成功。
- ビルド時に復元で変わる既存のlockファイルは、この機能差分に含めていない。新規依存パッケージなし。

## 未解決・実機確認が必要な点

- **ゲーム内の2クライアントでの動作は未検証**。30fps試験は決定ロジックのシミュレーションであり、実ゲームのFPS・追従遅延を測定したものではない。
- 各都市・住宅街・Custom転送網・クレセントの実機網羅、通信断、混雑した転送UI、未開放マウント飛行、IPCのバージョン差を確認する必要がある。
- 古いAltMateクライアントは飛行/コンテンツ状態を送らないため、両側をこの版に揃えて検証すること。
- BMR起動成功はコマンド受付で確認し、実移動は進捗監視で判定する。BMR側の全設定をスナップショット復元する機構は今回含めていない。手動のBMR設定変更を同時に行う場合の完全な所有権保証はない。
- vnavmesh自体には利用者単位の移動リースがない。先に動いている他者移動や経路計算中に始まった他者移動には譲るが、AltMateの経路走行中に他者が同じグローバル経路を置き換えるケースまでは識別できない。
- Wrathは競合監視のみ。Wrathの自動起動・設定書換え・専用リース取得は行わない。IPCが使えない場合は競合回避を優先しRSR開始を待機する。
- 設定階層の対象は上記4項目。他の戦闘/転送設定は既存の共通設定を維持する。
- 通常追従のBMR優先を使う場合は、連携操作画面の新チェック項目をONにする。
