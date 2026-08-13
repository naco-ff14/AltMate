# AltMate

AltMate is a Dalamud plugin for operating multiple FFXIV clients on one PC. The UI supports Japanese and English.

## Installation (custom Dalamud repository)

Add the following URL to **Dalamud Settings → Experimental → Custom Plugin Repositories**, save the settings, and install AltMate from the plugin installer.

`https://raw.githubusercontent.com/naco-ff14/AltMate/main/repo.json`

The repository URL starts working after this source is pushed to the public `naco-ff14/AltMate` GitHub repository and its first tagged release has produced `latest.zip`.

## Privacy and local data

AltMate does not contain hard-coded character names, account identifiers, PC user names, credentials, or API tokens. It does not send character data to an external service.

To share state between separate Dalamud installations on the same PC, AltMate stores a local JSON file at `%LOCALAPPDATA%\\AltMate\\shared-config.json`. It can contain character and retainer names, Content IDs, Free Company IDs, housing records, and gil snapshots. Local coordination uses loopback-only UDP multicast and a randomly generated installation key. Treat the shared JSON file and Dalamud logs as private data; do not attach them to public bug reports without redacting them.

## Optional integrations

- Lifestream: synchronized teleport and aethernet travel
- Penumbra: effective changed-emote discovery and playback
- BossMod Reborn / Rotation Solver Reborn: optional combat coordination

## Build

Requires the Dalamud .NET SDK API level 15. Build with `dotnet build -c Release`. Runtime configuration, `bin`, and `obj` are not part of the source distribution.

## Publishing a release

1. Keep the versions in `AltMate.csproj` and `repo.json` identical.
2. Commit and push the release changes to `main`.
3. Create and push a matching tag, for example `v1.18.0.0`.
4. The `Release AltMate` workflow builds and validates `latest.zip`, then attaches it to a GitHub Release.
5. Confirm that `https://github.com/naco-ff14/AltMate/releases/latest/download/latest.zip` downloads the package.

The ZIP contains `AltMate.dll`, `AltMate.json`, and `images/icon.png` at the package root layout expected by Dalamud.

## v1.18.0

- Added Japanese/English language selection under Settings
- Added local link-key validation to ignore unrelated AltMate multicast packets
- Documented locally stored identifiers, privacy behavior, and optional integrations
- Updated public plugin metadata and distribution exclusions
- Configured GitHub Actions to download the Dalamud development assemblies before building

## v1.17.0

- 左メニューを「ホーム→連携操作→アニメーション→ハウジング→ギル管理」の順に整理
- 設定は従来どおり左メニュー最下部に配置
- ホームをハウジング応募状況・連携状態・ギル概要のダッシュボードへ変更
- ホームの「ハウジングを確認」ボタンを削除
- 各サマリーカードをクリックすると対応する機能ページへ移動

## v1.16.1

- v1.16.0以前から存在する応募記録の預かり額が0になる問題を修正
- 現在周期の応募先を保存済み空き土地と照合し、土地価格を自動補完
- 構造化住所がない旧記録も住所文字列から照合
- 過去周期・結果確認済みの応募記録は補完・預かり集計の対象外
- 一致する価格情報がない応募は「金額未取得」として件数表示

## v1.16.0

- ハウジング抽選応募時に支払った土地代をキャラクターごとに記録
- ギル管理へ「ハウジング抽選預かり中」を青色で表示
- 使用可能ギルと抽選預かり中を分離し、総資産には両方を加算
- 結果確認後および抽選周期変更後は預かり中の集計から除外

## v1.15.1

- ギル合計計算で`ulong`をLINQ `Sum`へ渡していたビルドエラー2件を修正

## v1.15.0

- 新機能「ギル管理」を左メニューへ追加
- 全キャラクター本人と各リテイナーの最新所持ギルを保存・一覧表示
- FCチェスト内ギルをFC ID単位で保存し、同じFC所属キャラクター間の重複を排除
- キャラクター・リテイナー・FCを含む合計額を表示
- 既存の共有設定機構を使い、別クライアント／別Dalamud環境へ即時共有

## v1.14.0

- Modを先に選択するアニメーションUIを廃止
- PenumbraのChanged Itemsと同様に、現在有効なエモートを一覧表示
- エモート名・実際の適用元Mod・ID・再生ボタンを表形式で表示
- 上部で再生するリーダー／サポーターを選択
- エモート名とMod名の両方を検索可能

## v1.13.3

- 無効なPenumbra Modや未選択オプションのエモートが一覧へ出る問題を修正
- 現在のキャラクターに割り当てられた実効コレクションを基準に表示
- 同一エモート競合時はPenumbraの優先度判定で実際の適用元になったModだけ表示

## v1.13.2

- Penumbra最新版のChanged Items IPC名 `Penumbra.GetChangedItems.V5` に対応
- 取得失敗時、原因調査用に例外種別を画面へ表示

## v1.13.1

- PenumbraのChanged Itemsにある項目種別が日本語の場合、エモートを検出できなかった問題を修正
- 半角／全角コロン、末尾のエモートID、接頭辞の表示言語に依存せずゲーム内エモート名と照合

## v1.13.0

- 新機能「アニメーション」を左メニューへ追加
- Penumbraの公開IPCからMod一覧とChanged Itemsを取得
- Changed Itemsに含まれるエモートを一覧から再生
- リーダー／サポーターを再生先として選択可能
- 実際の差し替え結果は、再生先キャラクターのPenumbraコレクションとMod優先度を尊重

## v1.12.1

- リーダーのターゲット指定がゲーム側へ反映されたことを確認してから`/follow`を実行
- 対象が表示範囲外・ターゲット不可・名前不一致の場合は追従コマンドを送信しない
- 距離が離れた際の「1番目にターゲット名の指定がありません」エラー連打を防止

## v1.12.0

- リーダーと現在ワールドが異なる場合、追従・戦闘・テレポ・都市／住宅街移動など全自動連携を停止
- フォロワー側に「リーダーの元へ移動」ボタンを追加
- 明示操作時のみLifestreamでリーダーのワールドと現在エリアのエーテライトへ合流
- 直接移動できるエーテライトがない特殊エリアではワールド移動まで実行して理由を表示

## v1.11.2

- Dalamudプラグイン一覧の設定ボタンからAltMateの設定ページを直接開けるよう対応
- プラグイン検証警告を解消するためマニフェストの詳細説明を拡充

## v1.11.1

- ログアウト遷移中は状態送信・追従・戦闘・移動処理を即時停止
- CurrentWorldやClassJobが破棄された後に参照していた連続例外を修正
- 状態パケット作成全体を例外保護し、ログアウト時の実行状態をリセット

## v1.11.0

- `%LOCALAPPDATA%\\AltMate\\shared-config.json`で別Dalamud環境間の設定を共有
- 名前付きMutex・最新ファイル再読込・一時ファイル置換で同時書き込みに対応
- キャラクター情報はContent ID単位、空き土地はワールド・住宅街・区単位で最新確認結果をマージ
- 設定更新をAltMate間で即時通知し、通知欠落時も3秒間隔のRevision確認で再読込
- リーダー指定と共通動作設定は共有し、各クライアントの実行状態とウィンドウ状態は分離

## v1.10.0

- 都市内エーテライトの同一マップ移動判定を40mから12mへ調整
- 都市内エーテライトによる別マップへの移動をAethernetGroup照合付きで同期
- Lifestreamが受付待ちの場合は移動候補を保持し、期限内に自動再試行
- 移動受付時に追従を停止し、行動ステータスへ移動中と表示

## v1.9.9

- 最小化ボタンを通常画面内からタイトルバー右上へ移動
- コンパクト表示の背景を半透明化

## v1.9.8

- 最小化中のタイトルバーを非表示化し、コンパクトな横長バーだけを表示
- 最大化すると通常のタイトルバーとウィンドウ設定を復元

## v1.9.7

- 最小化表示を横長の2段レイアウトへ変更
- 1段目に小型ロゴ・緊急停止・アイコン型の最大化ボタンを配置
- 2段目に追従中・待機中など、現在の行動ステータスを表示

## v1.9.6

- 左メニューに最小化ボタンを追加
- 最小化中は小さなプラグインロゴ・緊急停止・最大化ボタンだけを表示
- 最大化時は最小化前のウィンドウサイズへ復元

## v1.9.5

- AltMate内のトレード自動化とアイテム移動メニューを削除（アイテム移動はDropBoxを利用）
- 左メニューの連携キャラクター名を角括弧付きのアクセントカラーで表示

## v1.9.4

- 相乗り要求をB自身ではなく、乗せてもらうAのキャラクターへ送るよう修正
- Bがマウント中・相乗り中・5mより遠い場合は不正な相乗り要求を送信しない

## v1.9.3

- 相乗りをキャラクター名コマンドではなく対象キャラクターへの直接要求へ変更
- 都市エーテライトのID変化を12秒間保持し、後続の座標ジャンプと結び付け
- ID変化と座標変化が別フレームで届くことで都市内同期を見失う問題を修正

## v1.9.2

- ゲーム側の自動追尾キャンセルを検出して内部追従状態を解除
- 追尾キャンセル後、距離条件を満たせば自動で追従を再開
- `/follow`実行直後の状態反映待ちとして1秒の猶予を追加

## v1.9.1

- Aを現在ターゲットへ設定してから引数なしの`/follow`を実行
- 空白を含むキャラクター名を`/follow`の引数へ渡す問題を修正
- 追従開始済みの場合は追従コマンドを連続送信しない

## v1.9.0

- フレンドハウステレポ機能を完全に廃止
- AのFC住宅テレポ時、BはLifestreamの住所移動のみ使用
- フレンドリスト・専用画面・テレポ候補一覧へのアクセスを削除

## v1.8.9

- クラッシュ原因となる未定義UIのネイティブノード走査を停止
- フレンドハウステレポ画面の自動表示は維持し、住宅選択は安全のため手動へ戻す
- 画面表示中はLifestreamへのフォールバックと追従を停止

## v1.8.8

- 実際のDalamud APIで公開されていない`AtkUnitBasePtr.Struct`への依存を削除
- フレンドハウステレポ画面のポインタを`Address`から安全に明示変換

## v1.8.7

- フレンドハウステレポに個人宅・FC宅・アパルトメントが並ぶ仕様を考慮
- 種類名ではなく住宅街・区・番地の完全一致で対象住宅を選択
- 個人宅が対象でも自動選択できるよう照合条件を修正
- アパルトメントの自動選択は棟・部屋番号の同期追加後に対応予定

## v1.8.6

- フレンドハウステレポ一覧から住宅街・区・番地が一致するFC住宅を自動選択
- 一致する住所がない場合は誤テレポせず手動選択で待機
- FC住宅選択後のテレポ確認を自動承認

## v1.8.5

- 無効な`/follow off`を廃止し、追従中のみ`/automove off`で停止
- フレンドハウステレポ画面表示中は予約済み追従コマンドを破棄
- テレポ前後に「ターゲット名 off」エラーが連続する問題を修正

## v1.8.4

- B側でAのフレンド情報を取得してからフレンドハウステレポ画面を開くよう変更
- 専用画面が開くまで最大4回再試行
- 専用画面の表示を検出し、動作確認中は住所移動へ進まないよう待機

## v1.8.3

- 一般フレンドのFCハウスへ、フレンドリスト専用のハウステレポ処理で移動
- `ProcessChatBoxEntry`で展開されない`<t>`をキャラクター実名へ置換
- Aが表示範囲外になった際の「ターゲット名が正しくありません」連続エラーを修正

## v1.8.2

- FC住宅とフレンド住宅を住宅固有の `HouseId` で照合
- 住所照合も併用し、住宅種別の見え方が異なる場合に対応
- フレンドテレポ候補を8秒間再取得してから住所移動へフォールバック

## v1.8.1

- FCハウスの住所移動中はAltMateの追従・相乗りを停止
- Lifestreamの処理完了を確認し、3秒後に追従を再開
- Lifestreamの経路移動が `/follow` に上書きされる問題を修正

## v1.8.0

- リーダーがFCハウスへテレポした際、ワールド・住宅街・区・番地をフォロワーへ共有
- フォロワーのテレポ一覧に同じ住宅があれば、フレンドテレポを優先して直接移動
- フレンドテレポ不可の場合はLifestreamで同じ住所の区画前へ移動
- FCハウス内部への入室は行わない
- FCハウス移動の個別設定と状態表示を追加

## v1.7.1

- Dalamud API 15の新しい `IChatGui.ChatMessage` 形式へ対応し、CS0123を解消

## v1.7.0

- リーダーの通常テレポ先をフォロワーへ共有し、Lifestreamで同じ目的地へ移動
- 都市内エーテライトの到着先を検出して同期
- 住宅街専用エーテライトの到着先を検出して同期
- 通常テレポが一時的に実行できない場合は45秒間再試行
- 移動同期3項目と状態表示を連携操作画面へ追加
- ハウス内部への自動入室は誤操作防止のため今回の対象外

## v1.6.4

- AltMateの戦闘連携が出すBMR設定完了メッセージ4種をチャットから非表示
- BMRのエラーやその他の通知は表示を維持

## v1.6.3

- リーダーが表示範囲外になった場合、対象指定コマンドを送信せず静かに中止
- 追従・相乗りコマンドの送信直前に対象を再確認

## v1.6.2

- 2クライアント連携時の受信処理をFrameworkスレッドへ集約
- UDP送信の同時実行を防止し、終了処理を安全化

複数キャラクターの管理と操作を支援するDalamudプラグインです。

## v1.6.0

- UIの「操作役A／追従役B」を「リーダー／フォロワー」へ統一
- 3キャラクター以上でも役割が分かりやすい表記へ変更

## v1.6.1

- 連携操作画面のクレセントアイル関連設定を折り畳み式へ変更
- 初期状態では閉じ、必要なときだけ設定と状態を展開

## v1.2.0

- クレセントアイル南征編・北征編で、操作役Aのエーテライト移動先を検出
- 追従役BのLifestreamへ同じ目的地を送り、エリア内移動を同期
- Bがエーテライト付近にいない場合は20秒待機し、Lifestream処理中は自動再試行

## v1.3.0

- クレセントアイル内でAがデジョンを開始したらBもデジョン
- B側のコンテンツファインダー突入確認を自動承認
- B側に届いたパーティのテレポ勧誘を自動承認

## v1.3.1

- デジョン詠唱の識別値を修正
- B側のデジョンをチャットコマンドではなくGeneralActionから直接実行

## v1.3.2

- Aのデミデジョン入力をActionManagerで直接検出し、専用イベントを3回送信
- Bの追従予約を解除してからデミデジョンし、A消失後の `<t>` エラーを抑止
- Bがマウント中の場合は解除後に再試行

## v1.3.3

- ActionManagerのフック初期化をunsafeコンテキストへ修正し、CS0214を解消

## v1.3.4

- デミデジョン確認文「開始地点に戻ります」を検出して「はい」を自動選択

## v1.3.5

- Aが確認で「はい」を選び、デミデジョンの詠唱が始まってからBへ通知
- Aが「いいえ」で取り消した場合にBだけ移動する問題を防止

## v1.3.6

- デミデジョン固有の詠唱ID判定を廃止
- Aの確認画面で「はい」が押された瞬間を直接検出してBへ通知

## v1.4.0

- クレセントアイル内で2m以内の宝箱を検出して自動で開ける機能を追加
- 戦闘中・詠唱中・イベント中は宝箱操作を停止
- 連携操作UI内のクレセントアイル関連設定を専用の表示枠へ分離

## v1.4.1

- クレセントアイルの宝箱をマウントに乗ったまま開けるよう修正

## v1.5.0

- AltMate専用アイコンを追加
- Dalamudプラグイン一覧用の `images/icon.png` をビルド出力へ同梱
- 左メニュー上部にアイコンを表示

## v1.5.1

- 左メニューのアイコンを128pxへ拡大
- アイコンとタイトルを別の行へ分離し、アイコンを中央配置

## v1.5.2

- 左メニューの連携操作に、接続中のキャラクター名を表示
- 複数接続時は先頭キャラクター名と残り人数を表示
現在はハウジング抽選状態と空き土地の管理に対応し、今後は複数キャラクター同時操作やアイテム受け渡し支援を追加します。

UIは左側にホーム・ハウジング・連携操作・アイテム移動・設定の大項目メニュー、右側に選択中の機能を配置しています。表示キャラクターの設定はハウジング内にあります。

## 連携操作

- 同じPC上で起動している複数のFF14クライアントを自動検出します。
- 操作役Aと追従役Bを設定し、ジョブ・HP・戦闘・マウント状態を一覧表示します。
- BはAとの距離が開くと自動追従します。
- Aが複数人乗りマウントに乗ると、Bは接近して1番席への相乗りを試みます。
- 戦闘・ロード・カットシーン・会話・製作・採集中は安全のため自動操作を停止します。
- 「すべて緊急停止」で、そのクライアントの追従と自動操作を即時停止できます。
- Aの戦闘開始をBへ同期し、BossMod Rebornで移動・ターゲット、Rotation Solver Rebornで攻撃ローテーションを開始できます。
- Aの戦闘終了後は指定した猶予時間でBMRとRSRを自動停止します。

## 使い方

- `/altmate` でAltMateを開きます。旧コマンドの `/hlt` も引き続き利用できます。
- 各キャラクターでログインすると、そのキャラクターが一覧に追加されます。
- 応募期間中の「未参加」、結果発表期間中の「未確認」のときだけログイン時に自動表示します。
- 表示設定で選択したキャラクターだけ、一覧表示とログイン時の自動表示の対象になります。
- ドキュメント内のFF14キャラクターフォルダからContent IDを読み取り、未ログインのキャラクターも一覧へ追加します。
- プラグイン導入後に土地の抽選へ応募すると、応募先と応募期間終了日時を自動記録します。
- 応募期間中は「参加／未参加」、結果発表期間は「未確認／確認済」を色分けして表示します。
- 結果を確認・受領すると、そのキャラクターの記録を完了扱いにします。
- 住宅街エーテライトから区画一覧を手動確認すると、取得した空き土地を「空き土地」タブへ保存します。
- 同じ区を再確認した場合は、その区の保存内容を最新情報で置き換えます。
- 空き土地をサイズ（ALL/S/S-M/M/M-L/L）とワールドで絞り込めます。
- 空き土地の行をクリック、または右クリックメニューから、ゲーム内の住宅街マップに該当番地の旗を表示できます。
- 保存中の応募先と一致する土地は行を緑色にし、同じ土地へ応募中のキャラクター数を表示します。
- 新しい応募期間へ切り替わった時点で、前周期の空き土地一覧を自動的に消去します。
- Lifestream導入時は、空き土地の右クリックメニューから対象ワールド・住宅街・区・番地へ自動移動できます。

## 注意

ゲームは他キャラクターの状態をログイン中に問い合わせるAPIを提供していません。そのため、他キャラクターについては最後にそのキャラクターで確認した状態を保存して表示します。

キャラクターフォルダには名前とワールド名が保存されていないため、まだログインしていないキャラクターはフォルダIDで表示されます。一度ログインすると名前とワールド名へ更新されます。

プラグイン導入前に行った応募は自動記録されません。次回の応募から自動追跡されます。

抽選周期は、2026年8月13日 0:00（日本時間）の応募期間開始を基準に9日周期で計算します。応募時にゲーム内から取得した締切日時でも自動補正します。

## ビルド

Visual Studio 2022 とDalamud開発環境で `AltMate.csproj` をビルドしてください。

Housing Herald (Fuwa Aika) の公開実装を、抽選画面イベントの調査資料として参照しています。

PaissaHouse (Andrew Zhu) の公開実装を、住宅区画情報の調査資料として参照しています。
