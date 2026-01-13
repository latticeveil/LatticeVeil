# ⚒️ RedactedCraft C# (v6.0.0)

![License](https://img.shields.io/badge/license-MIT-green)
![Platform](https://img.shields.io/badge/platform-win--x64-blue)
![Engine](https://img.shields.io/badge/engine-MonoGame-orange)

**RedactedCraft** is a high-performance voxel sandbox engine built from the ground up in C# using the MonoGame framework.

---

## 🛠️ The Artificer Update (v6.0.0)
This major update consolidates all assets and engine features into a single, unified version. 
- **Consolidated Assets:** Unified V6 asset pack.
- **Lore Integration:** Full support for The Continuist Papers and faction lore.
- **Nullblock Bedrock:** Unbreakable bottom layer enforced at Y=0.

---

## 🚀 Quick Start

| Artifact | Download |
| :--- | :--- |
| **Latest Build** | [🎮 Download RedactedCraft.exe](./RedactedCraft.exe) |
| **Source Archive** | [📦 Download RedactedcraftCsharp.zip](./RedactedcraftCsharp.zip) |
| **Asset Pack** | [📦 Download Assets.zip](./Assets.zip) |

### Prerequisites
- Windows 10/11 (x64)
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## ✨ Features

- **🌍 Infinite Terrain:** Procedurally generated worlds with varied biomes.
- **⚡ Greedy Meshing:** Optimized voxel rendering for high FPS.
- **👥 Multiplayer:** Integrated LAN discovery and Online play via Epic Online Services (EOS).
- **🛠️ Extensible:** Easy-to-modify block registry and asset system.
- **🚀 Lightweight Launcher:** Built-in launcher for updates, profiles, and settings.

---

## Lore Pack

- Lore overview: README_LOREPACK.md
- Change log: RELEASE_NOTES.txt

## ⌨️ Controls

| Key | Action |
| :--- | :--- |
| **W, A, S, D** | Move |
| **Space** | Jump / Fly (Double-tap) |
| **Left Click** | Mine / Break Block |
| **Right Click** | Place Block |
| **E** | Open Inventory |
| **T** | Open Chat |
| **Esc** | Pause Menu |

---

## 🛠️ Development

To build from source:
1. Clone the repo: `git clone https://github.com/Redactedcraft/Redactedcraft.git`
2. Open `RedactedcraftCsharp.sln` in Visual Studio 2022.
3. Restore NuGet packages and Build (Release/x64).

> **Note:** Sensitive EOS SDK tools are excluded from this repository for security. Runtime DLLs are included in `ThirdParty/EOS`.

---

## 📜 License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.