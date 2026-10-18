# セキュリティ

## 脆弱性の報告

脆弱性を見つけた場合は、公開のIssueには書かず、このリポジトリの **Security** タブの **Report a vulnerability**（GitHubの非公開の報告: https://github.com/ironbeer/io-recorder/security/advisories/new ）から知らせてください。再現手順・影響・確認したバージョンがあると助かります。受け取ったら内容を確認して返信し、修正したら更新履歴でお知らせします。

Please report security vulnerabilities privately through GitHub's private vulnerability reporting (https://github.com/ironbeer/io-recorder/security/advisories/new), not in public issues. Include the steps to reproduce, the impact and the version you checked if you can. We will confirm the report, keep you informed while we work on a fix, and disclose the issue in a security advisory and the release notes once a fixed release is available.

## 対象

最新のリリースと `main` ブランチを対象にします。

## このツールが使う権限

IO Recorderは、ETWでファイルI/Oを記録するために管理者権限で動きます。使う権限・実行する外部プログラム・通信の有無・記録に入る情報は、READMEの「[権限とセキュリティ](README.md#権限とセキュリティ)」にまとめています。

## 配布物の確かめ方

リリースのzipには、SHA-256の値（`.sha256`）を添えています。

```bash
sha256sum -c IORecorder-v0.4.0-win-x64.zip.sha256
```

v0.4.0以降のリリースでは、zipと中の実行ファイル（`IORecorder.exe`・`iorecorder-cli.exe`）が、このリポジトリのコードからGitHub Actionsでビルドされたことを、GitHub CLIで確かめられます（ビルドの来歴の証明）。v0.1.0〜v0.3.0は、リポジトリを公開する前にビルドしたので、この証明はありません。

```bash
gh attestation verify IORecorder-v0.4.0-win-x64.zip -R ironbeer/io-recorder
```

同じ証明をリリースのファイル（`IORecorder-v0.4.0-provenance.sigstore.json`）としても添えているので、GitHubのAPIを使わずに確かめることもできます。

```bash
gh attestation verify IORecorder-v0.4.0-win-x64.zip --bundle IORecorder-v0.4.0-provenance.sigstore.json -R ironbeer/io-recorder
```
