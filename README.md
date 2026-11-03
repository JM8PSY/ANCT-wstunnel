# ANCT-wstunnel

学内LANがHTTPプロキシ経由の通信しか許可しない環境で、`wstunnel`（WebSocketトンネル）の上に
WireGuardを通し、自前のVPS経由でインターネットに出るための構成一式です。

旭川高専（ANCT）の学内LANは、出口がプロキシ `websec.asahikawa-nct.ac.jp:8080` のみで、
WireGuard/Tailscale等はポートとDNSの両方で遮断されています。一方で 443 への HTTPS `CONNECT`
は通るため、`wstunnel`（WSS on :443）でVPSへ繋ぎ、その中でWireGuardを張ります。

## 構成

- **VPS側（サーバ）** — `wstunnel` サーバ + WireGuard の構築スクリプト。ユーザーごとに
  自分のVPSを用意する想定です。実装済み。
- **Windowsアプリ（クライアント）** — TailscaleのようなシンプルなON/OFFトンネルアプリ。
  接続先はVPSが生成する「接続バンドル(JSON)」を読み込むだけ。開発予定。

```
vps/            VPSセットアップスクリプト（実装済み）
  setup.sh      エントリポイント
  lib/          鍵生成・レンダリング・ネットワーク・インストール処理
  tests/        bats テスト
  README.md     VPS側の詳細手順
  E2E-CHECKLIST.md  実機での疎通確認チェックリスト
docs/superpowers/
  specs/        要件定義・設計ドキュメント
  plans/        実装計画（Plan A: VPS / Plan B: Windowsアプリ）
LICENSE         GPL-3.0 全文
```

## 使い方（VPS側）

クリーンなUbuntu LTS（amd64/arm64、root権限）で:

```bash
sudo bash vps/setup.sh --host <あなたのVPSのDDNS名>
# または
sudo bash vps/setup.sh --ip <IPアドレス>
```

実行すると接続バンドル（既定 `./wgvpn-bundle.json`）が生成されます。これをWindowsクライアントに
読み込ませて接続します。詳細は [`vps/README.md`](vps/README.md) を参照してください。

## 開発

```bash
bats vps/tests
shellcheck -x vps/setup.sh vps/lib/*.sh
```

CIで上記を自動実行します（`.github/workflows/vps-ci.yml`）。

## 注意

- 学内での利用は学校のネットワーク利用規約に抵触する可能性があります。利用は各自の責任で、
  必要に応じて情報システム部門に確認してください。
- SNI詐称などの偽装・回避手法は用いません。ブロックリストに無いホスト名・ポートをそのまま使う構成のみです。
- 秘密鍵・接続バンドルは機密情報です（パーミッション600）。ログや公開リポジトリに出さないでください。

## ライセンス

Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club.

本ソフトウェアはフリーソフトウェアです。GNU General Public License（Free Software Foundation
発行）のバージョン3、または（任意で）それ以降のバージョンの条件の下で、再配布および改変ができます
（`GPL-3.0-or-later`）。全文は [`LICENSE`](LICENSE) を参照してください。
