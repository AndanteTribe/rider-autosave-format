# Auto-save C# Formatter

[![Build and verify](https://github.com/AndanteTribe/rider-autosave-format/actions/workflows/ci.yml/badge.svg)](https://github.com/AndanteTribe/rider-autosave-format/actions/workflows/ci.yml)
[![JetBrains Rider](https://img.shields.io/badge/JetBrains-Rider-000000?logo=rider)](#requirements)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

English | [日本語](README_JA.md)

Automatically format C# after automatic or manual saves in JetBrains Rider, using the built-in **Reformat Code** profile. Enable formatting independently for each project and configure code style through Rider's formatting settings.

## Requirements

One plugin package covers Rider **2026.1.3 through 2026.2.3.1**, from build `RD-261.25134.178` through `RD-262.10968.170`. Binary compatibility checks cover:

| JetBrains Rider | Checked build |
| --- | --- |
| 2026.1.3 | `RD-261.25134.178` |
| 2026.1.5.2 | `RD-261.27258.81` |
| 2026.2 | `RD-262.8665.328` |
| 2026.2.3.1 | `RD-262.10968.170` |

Check your build in **Help → About**. Builds outside this range are not supported. No separate JDK, .NET SDK, or Gradle installation is required.

## Installation

1. Open **Settings → Plugins → Marketplace** in Rider.
2. Search for **Auto-save C# Formatter**.
3. Select the plugin by **AndanteTribe** and click **Install**.
4. Restart Rider if prompted.

## Usage

1. Open your C# project.
2. Choose **Tools → Enable Native Auto-save Formatting**.
3. Edit a C# file and let Rider save automatically, or save it manually.

Use the same Tools menu item to turn formatting off. The setting is saved per project and is off by default for new projects.

To avoid overlapping formatting operations, disable **Actions on Save → Reformat and Cleanup Code** and other format-on-save plugins for the same files.

## How It Works

- Formatting runs after the initial save and saves the formatted result again. File watchers, including Unity, may detect multiple writes.
- Only writable local `.cs` files belonging to exactly one open project's content are eligible. Read-only, excluded, out-of-project, and shared files belonging to multiple open projects are skipped. File-extension matching is case-insensitive.
- Formatting uses **Reformat Code** only. It does not switch to Silent or Full Cleanup profiles.
- A pass may be skipped during indexing, when newer edits are still unsaved, or when the formatting profile is unavailable. Pending passes are also skipped if you disable the setting or close the project.

## License

[MIT License](LICENSE) © 2026 [AndanteTribe](https://github.com/AndanteTribe).

The Gradle Wrapper is licensed under [Apache-2.0](https://github.com/gradle/gradle/blob/v9.3.1/LICENSE). Its original launcher headers and embedded `META-INF/LICENSE` are retained.
