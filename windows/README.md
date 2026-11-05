# Windowsクライアント

VPS側（`vps/`）が生成する接続バンドル(JSON)を読み込み、ON/OFFだけで学内LANのHTTPプロキシ越しに
`wstunnel` + WireGuard のトンネルを張るWindowsアプリです。

> 状態: ベータ版です。作者の環境で接続を確認しましたが、[`E2E-CHECKLIST.md`](E2E-CHECKLIST.md) の全項目は確認できていません。
> 確認項目は [`E2E-CHECKLIST.md`](E2E-CHECKLIST.md) にあり、結果はそこに記録します。

## 構成

- `WgVpn.App` — トレイ常駐のWPF GUI（ON/OFF、状態、設定ファイル読み込み、ログ）。管理者権限は不要です。
- `WgVpn.Service` — サービス `WgVpnService`（LocalSystem）。`wstunnel.exe` と WireGuard
  （公式 WireGuard for Windows）の起動・停止、ルート設定を行います。GUIとは名前付きパイプで通信します。
- `WgVpn.Core` — 状態遷移・バンドル解析・設定生成など（テスト対象のロジック）。
- `installer/` — Inno Setup のインストーラとビルドスクリプト。

## インストール

1. 配布された `WgVpn-Setup-<version>.exe` をダブルクリックします。
2. UAC（このアプリがデバイスに変更を加えることを許可しますか?）に **1回だけ** 「はい」と答えます。
   管理者権限が必要なのはインストール時のみです。
3. インストーラは次を行います。
   - `C:\Program Files\WgVpn` に `WgVpn.App.exe`、`WgVpn.Service.exe`、`wstunnel.exe` とライセンスを配置
   - 公式 WireGuard for Windows が無ければ、同梱のMSI（署名者 WireGuard LLC、バージョンは
     [`VERSIONS`](VERSIONS) に固定）を無人インストール（既にあれば何もしません）
   - `C:\ProgramData\WgVpn` を SYSTEM と Administrators だけがアクセスできる設定で作成
     （接続バンドル＝秘密鍵がここに保存されます）。このフォルダーがリンク（ジャンクション等）だった場合や、
     所有者が SYSTEM/Administrators でない場合は、安全のためインストールを中止します
   - サービス `WgVpnService` を LocalSystem・自動起動で登録して開始（異常終了時は自動再起動）
   - 全ユーザーのログオン時に GUI をトレイ常駐で自動起動（HKLM の Run キー、`--tray` 付き）
   - 管理者ユーザーでは、インストール後に WireGuard 自身のトレイ/管理画面が空のトンネル一覧で表示されることがあります。
     このアプリは WireGuard のトンネルを `wgvpn` として自動で出し入れするだけなので、無視してください。
4. 完了画面の「ANCT WireGuard VPN を実行する」にチェックを入れたまま完了すると GUI が開きます（非管理者で起動します）。
   ログオン時の自動起動は `--tray` 付きで、ウィンドウは開かずトレイにだけ常駐します。
   以後、ON/OFF に UAC は出ません。サービスの登録・起動に失敗した場合はエラーが表示され、GUI は起動しません。
   その場合はもう一度インストーラを実行してください。

### SmartScreen の警告

署名していないため、ダウンロードしたインストーラを開くと「WindowsによってPCが保護されました」と
表示されることがあります。提供元をよく確認した上で、**「詳細情報」→「実行」** を選んでください。

### Smart App Control（重要）

SmartScreen の手順は **Smart App Control（SAC）には効きません**。開発機（SAC がオン）では、公式の
`wstunnel.exe` v11.1.0 が "An Application Control policy has blocked this file. Malicious binary
reputation" としてブロックされ、Microsoft Defender にも `Trojan:Win32/Kepavll!rfn` として隔離されました。
そのためインストーラには、同じ v11.1.0 のソースからビルドした `wstunnel.exe` を同梱しています。開発機では、これが
SAC オンのまま起動し、Defender の手動スキャンでも検出されませんでした。ただし確認できているのはこの1台のビルドだけで、
ほかのPCや別のビルドでブロックされないことは保証できません。SAC により実行がブロックされると、接続時にアプリが
エラーを表示します（トンネルは張れません）。

- SAC の状態は PowerShell で確認できます: `(Get-MpComputerStatus).SmartAppControlState`
- この設定を変更するかどうかは、そのPCの所有者が判断することです。このドキュメントでは回避手順を案内しません。
- このプロジェクトには署名済みビルドがありません（未署名配布）。
- SAC は未署名のインストーラ、`WgVpn.Service.exe`、`WgVpn.App.exe` もブロックする可能性があります。その場合、アプリ内には
  エラーは出ません（実行自体が阻止されます）。これは未検証です。確認できている事例は、この開発機のDebugビルドの
  `WgVpn.App.exe` が SAC オンのまま一度起動できた、という1件のみです。

## 使い方

1. トレイアイコンをダブルクリックするか、スタートメニューの「ANCT WireGuard VPN」で画面を開きます。
2. 「設定ファイルを読み込む」で、VPSで生成した `wgvpn-bundle.json` を選びます（上限 256 KiB、バージョン1のみ対応）。
   バンドルは秘密鍵を含みます。共有・公開せず、読み込み後に元のファイルが不要なら安全に削除してください。
3. トグルをONにすると接続します（トレイメニューの「接続」「切断」でも可）。状態は 未接続 → 接続中 → 接続済み と変わります。
4. OFFにすると切断し、ルートと WireGuard トンネルを片付けます。ウィンドウを閉じるとトレイに隠れ、
   トレイメニューの「終了」で GUI だけが終了します（サービスは動き続けます）。

## アンインストール

「設定」の「アプリ」から *ANCT WireGuard VPN* を削除します（管理者権限が必要）。

- GUI を終了し、サービスを停止（トンネルも片付け）して削除し、自動起動の登録を外します。
- `C:\ProgramData\WgVpn` を、接続バンドル（秘密鍵）ごと削除します。ただし、フォルダーが安全確認（リンク、所有者、権限）に通らない場合や削除に失敗した場合は残し、その旨とパスを表示してログにも記録します。残った場合は内容を確認して手動で削除してください。
- **WireGuard for Windows はアンインストールされません**。不要なら別途削除してください。

## トラブルシューティング

GUIに表示されるメッセージ（原文のまま）と対処です。

| 表示 | 意味と対処 |
|---|---|
| バックグラウンドサービスが起動していません | サービス `WgVpnService` が停止中です。`services.msc` で状態を確認して開始、または再インストール。 |
| `no bundle imported` | バンドル未読み込み。「設定ファイルを読み込む」から読み込む。 |
| `disconnect first` | 接続中・接続処理中に再度接続を要求した。いったん切断してからやり直す。 |
| `Cannot connect until the previous cleanup succeeds.` / `Cleanup incomplete, disconnect again to retry: ...` | 前回の後片付けが未完了。「切断」をもう一度押して再試行。続く場合は `route print` で 0.0.0.0/1・128.0.0.0/1 などの残りを確認。 |
| `No route to the proxy address ...` | プロキシ宛の経路を作れない（ネットワーク未接続など）。学内LANに接続しているか確認。 |
| `Could not store the bundle: ...` | バンドルの保存に失敗（読み込み時）。サービスの状態と `C:\ProgramData\WgVpn` を確認。 |
| `Connection failed: ...` | 上記以外の接続失敗。続く本文とGUIのログを確認。 |
| `Could not resolve proxy ... to an IPv4 address.` | プロキシ名を解決できない。学内LANに接続しているか確認。 |
| `Could not connect to the proxy on port ...` / `Proxy refused CONNECT: ...` | プロキシへ到達できない、またはCONNECTを拒否された。学内LANか、バンドルのサーバ/ポートを確認。 |
| `TLS pin preflight timed out` | サーバへのTLS事前確認が時間切れ。VPSの稼働とポート443を確認。 |
| `The server's TLS certificate does not match the fingerprint in the bundle.` | サーバ証明書がバンドルの `tlsSha256` と一致しない。VPSを作り直した場合は新しいバンドルを読み込む。心当たりがなければ接続を続けないこと。 |
| `Waiting for wstunnel's UDP listener timed out` / `wstunnel exited with code ...` / `Could not start wstunnel.exe: ...` | `wstunnel.exe` が起動しない・すぐ終了する。前述の Smart App Control によるブロックを最初に疑う。ウイルス対策ソフトの隔離も確認。 |
| `WireGuard handshake timed out` | WireGuardのハンドシェイクが成立しない。VPS側の `wg show` と、バンドルがVPSの最新のものか確認。 |
| `WireGuard for Windows is not installed.` | `C:\Program Files\WireGuard` が無い。インストーラを再実行。 |
| `Installing the WireGuard tunnel failed with exit code ...` / `Uninstalling the WireGuard tunnel failed ...` | WireGuard側のエラー。出力内容が続けて表示される。 |
| ログ: `読み込み失敗: ...` | バンドルの不備（JSONの形式、`Missing required field: ...`、未対応バージョンなど）。VPSで再生成する。 |
| ログ: `サービスに接続できません` / `要求は拒否されました: ...` | サービス未起動、または要求が拒否された。 |

## 設計上の仕様との差分

- **AllowedIPs とキルスイッチ**: バンドルの `0.0.0.0/0` は、WireGuard設定では `0.0.0.0/1` と `128.0.0.0/1`
  に分けて書き出します。`/0` のままだと WireGuard for Windows の WFP キルスイッチが有効になり、`wstunnel.exe`
  自身のプロキシへのLAN上のTCP接続まで遮断され、トンネルを張れないためです。その結果、**キルスイッチはありません**。
  トンネルが落ちる（`wstunnel.exe` の異常終了など）と、通信が直接外へ出る可能性があります。切断状態や異常時に
  外へ出られては困る通信には使わないでください。
- **DNS**: キルスイッチが無いため、ポート53の問い合わせを制限するものがありません。接続中でも Windows の
  マルチホームDNS解決により、学内リゾルバーへ名前解決の問い合わせが送られる可能性があります（VPN側のDNSだけが
  使われる保証はありません）。インターフェイスのメトリックは変更しない方針です。名前解決の漏れが困る用途には使わないでください。
- **TLS証明書の固定**: `wstunnel` には証明書ピン留めのフラグがありません。そのためサービスが接続前に
  サーバ証明書のSHA-256をバンドルの `server.tlsSha256` と照合（事前確認）し、不一致なら接続しません。

## 既知の制限とセキュリティ上の注意

- **接続状態の再確認はしません**: 最初のハンドシェイク成立後は接続状態を再確認しません。そのため、スリープからの復帰や
  ネットワーク切替のあと、トンネルが実際には使えなくなっていても GUI が「接続済み」のままになることがあります。
  その場合は OFF → ON を操作してください。
- **共有PCでの利用**: UAC なしで ON/OFF できる設計の結果、ログオンしている対話ユーザーなら誰でも
  設定ファイルを読み込んで接続でき、そのPC全体の通信経路が変わります。共有PCでは、利用者全員が
  この操作を行えることに注意してください。

## ビルド（ソースから）

前提: .NET 8 SDK、Inno Setup 6（`winget install JRSoftware.InnoSetup --scope user`）、git、rustup
（`winget install Rustlang.Rustup`。[`VERSIONS`](VERSIONS) に固定したツールチェーンを使います）、
Visual Studio の C++ ビルドツール。

```powershell
dotnet test windows/WgVpn.sln
powershell -File windows/installer/build.ps1                  # 成果物: windows/installer/output/WgVpn-Setup-<version>.exe
powershell -File windows/installer/build.ps1 -SkipInstaller   # publish まで（Inno Setup 不要）
```

`build.ps1` は [`VERSIONS`](VERSIONS) に固定した WireGuard for Windows の MSI をダウンロードして SHA-256 を
検証（不一致なら削除して中止）し、`wstunnel` を固定したコミット（v11.1.0）からビルドします（初回は10分程度）。
ビルドした `wstunnel.exe` が起動できない場合も中止します。続けて App と Service を自己完結・単一ファイルで
publish し、インストーラを作ります。バイナリはリポジトリに含めません（`windows/installer/build/` と
`output/` は git 管理外）。自己完結のためインストーラは約74 MiB（publish後の App 約154 MiB、
Service 約67 MiB、`wstunnel.exe` 約10 MiB）あります。

## 注意

- .NET 8 のサポートは 2026-11-10 に終了します。広く配布する前に、サポート中のバージョンへ移行してください。
- 学内での利用は学校のネットワーク利用規約に抵触する可能性があります。利用は各自の責任で、
  必要に応じて情報システム部門に確認してください。
- 接続バンドル（秘密鍵入り）をログやリポジトリ、チャットに載せないでください。

## ライセンス

GPL-3.0-or-later（リポジトリ直下の [`LICENSE`](../LICENSE)）。同梱の `wstunnel`（erebe/wstunnel、
BSD 3-Clause。`LICENSE-wstunnel.txt` を参照）と WireGuard for Windows は、それぞれのライセンスに従います。
