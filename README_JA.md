# Auto-save C# Formatter

[![Build and verify](https://github.com/AndanteTribe/rider-autosave-format/actions/workflows/ci.yml/badge.svg)](https://github.com/AndanteTribe/rider-autosave-format/actions/workflows/ci.yml)
[![JetBrains Rider](https://img.shields.io/badge/JetBrains-Rider-000000?logo=rider)](#動作要件)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

[English](README.md) | 日本語

JetBrains Rider の自動保存・手動保存後に、標準の **Reformat Code** プロファイルで C# を自動整形します。プロジェクトごとに有効・無効を切り替え、コードスタイルは Rider の整形設定で指定できます。

## 動作要件

次の Rider ビルドに対応しています。

| JetBrains Rider | 必要なビルド番号 |
| --- | --- |
| 2026.1.3 | `RD-261.25134.178` |
| 2026.2.3.1 | `RD-262.10968.170` |

ビルド番号は **Help → About** で確認できます。別のパッチリリースを含む他のビルドには対応していません。JDK・.NET SDK・Gradle を別途インストールする必要はありません。

## インストール

1. Rider で **Settings → Plugins → Marketplace** を開きます。
2. **Auto-save C# Formatter** を検索します。
3. **AndanteTribe** のプラグインを選択し、**Install** をクリックします。
4. 再起動を求められた場合は、Rider を再起動します。

## 使い方

1. C# プロジェクトを開きます。
2. **Tools** メニューから自動整形を有効にします。
3. C# ファイルを編集し、Rider の自動保存を待つか、手動で保存します。

同じ Tools メニュー項目で OFF にできます。設定はプロジェクトごとに保存され、新規プロジェクトでは初期状態で OFF です。

整形処理の競合を避けるため、同じファイルを対象とする **Actions on Save → Reformat and Cleanup Code** や他の保存時整形プラグインは無効にしてください。

## 動作について

- 最初の保存後に整形し、整形結果を再保存します。Unity などのファイル監視ツールが複数回の書き込みを検知することがあります。
- 対象は、開いている 1 つのプロジェクトのコンテンツに属する、書き込み可能なローカル `.cs` ファイルです。読み取り専用、除外対象、プロジェクト外、開いている複数のプロジェクトに属する共有ファイルは整形しません。拡張子の大文字・小文字は区別しません。
- **Reformat Code** のみを使用し、Silent / Full Cleanup プロファイルには切り替えません。
- インデックス作成中、新しい編集内容が未保存の場合、整形プロファイルが利用できない場合は、整形を見送ることがあります。設定を OFF にするか、プロジェクトを閉じると、待機中の整形もスキップします。

## ライセンス

[MIT License](LICENSE) © 2026 [AndanteTribe](https://github.com/AndanteTribe)。

Gradle Wrapper は [Apache-2.0](https://github.com/gradle/gradle/blob/v9.3.1/LICENSE) ライセンスです。ランチャーの元のヘッダーと JAR 内の `META-INF/LICENSE` を保持しています。
