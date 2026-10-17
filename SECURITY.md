# セキュリティ

## 脆弱性の報告

脆弱性を見つけた場合は、公開のIssueには書かず、このリポジトリの **Security** タブの **Report a vulnerability**（GitHubの非公開の報告）から知らせてください。再現手順・影響・確認したバージョンがあると助かります。受け取ったら内容を確認して返信し、修正したら更新履歴でお知らせします。

Please report vulnerabilities privately through **Security → Report a vulnerability** on this repository, not in public issues.

## 対象

最新のリリースと `main` ブランチを対象にします。

## このツールが使う権限

IO Recorderは、ETWでファイルI/Oを記録するために管理者権限で動きます。使う権限・実行する外部プログラム・通信の有無・記録に入る情報は、READMEの「[権限とセキュリティ](README.md#権限とセキュリティ)」にまとめています。

## 配布物の確かめ方

リリースのzipには、SHA-256の値（`.sha256`）を添えています。公開リポジトリのリリースでは、zipと中の実行ファイルが、このリポジトリのコードからGitHub Actionsでビルドされたことを、GitHub CLIで確かめられます。

```bash
gh attestation verify IORecorder-v0.1.0-win-x64.zip -R ironbeer/io-recorder
```
