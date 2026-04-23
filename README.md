<div align="center">

<img src="Icon.png" width="96" alt="MD Viewer logo"/>

# MD Viewer

**A fast, lightweight Markdown viewer and editor for Windows**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square&logo=windows)](https://github.com/Hackerprod/MD-Viewer)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Release](https://img.shields.io/github/v/release/Hackerprod/MD-Viewer?style=flat-square&color=orange)](https://github.com/Hackerprod/MD-Viewer/releases)

</div>

---

## ✨ Features

| | |
|---|---|
| 🖥️ **GitHub-style preview** | Renders Markdown with the same look and feel as GitHub — headings, tables, task lists, code blocks, emojis and more |
| ✏️ **Split-pane editor** | Toggle edit mode to get a live side-by-side editor and preview that updates as you type |
| 🗂️ **Multi-tab** | Open multiple files at once, drag & drop, middle-click to close |
| 🌓 **Dark & light themes** | Instant toggle with no flash on startup — preference is persisted |
| 🔍 **Find in document** | `Ctrl+F` opens an inline search bar with next/previous navigation |
| 💾 **Save & Save As** | Edit and save directly from the app; unsaved tabs show a `●` indicator |
| 👁️ **Auto-reload** | File watcher detects external changes and reloads automatically |
| 🚀 **Single instance** | Opening a second `.md` file focuses the existing window via named-pipe IPC |
| 📌 **Session restore** | Re-opens your last tabs on startup |
| 🪟 **Native Windows 11** | Title bar color follows the active theme via DWM API |

---

## 📸 Screenshots

<div align="center">

### Dark Theme
![Dark theme preview](https://raw.githubusercontent.com/Hackerprod/MD-Viewer/master/Assets/screenshot-dark.png)

### Light Theme
![Light theme preview](https://raw.githubusercontent.com/Hackerprod/MD-Viewer/master/Assets/screenshot-light.png)

</div>

---

## ⚡ Getting Started

### Requirements

- **Windows 10/11** (x64)
- [**.NET 10 Runtime**](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [**Microsoft Edge WebView2 Runtime**](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) *(usually pre-installed on Windows 11)*

### Installation

1. Download the latest release from [**Releases**](https://github.com/Hackerprod/MD-Viewer/releases)
2. Extract and run `MDViewer.exe`
3. *(Optional)* Go to **Tools → Associate .md Files** to open Markdown files directly from Explorer

### Build from source

```bash
git clone https://github.com/Hackerprod/MD-Viewer.git
cd MD-Viewer
dotnet build -c Release
```

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open file(s) |
| `Ctrl+W` | Close current tab |
| `Ctrl+E` | Toggle edit mode |
| `Ctrl+S` | Save *(in edit mode)* |
| `Ctrl+Shift+S` | Save As… |
| `Ctrl+D` | Toggle dark / light theme |
| `Ctrl+F` | Find in document |
| `F3` / `Shift+F3` | Find next / previous |
| `F5` / `Ctrl+R` | Reload file |
| `F11` | Full screen |
| `Ctrl+Tab` *(drag)* | Scroll tab strip |
| Middle-click tab | Close tab |

---

## 🛠️ Built With

| Library | Version | Purpose |
|---|---|---|
| [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) | 6.3.1 | Markdown source editor |
| [Markdig](https://github.com/xoofx/markdig) | 0.40.0 | Markdown → HTML rendering |
| [Microsoft WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) | 1.0.3240 | HTML preview |
| WPF / .NET 10 | 10.0 | UI framework |

---

## 📄 License

MIT © [Hackerprod](https://github.com/Hackerprod)
