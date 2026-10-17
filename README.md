<div align="center">
  <img src="Assets/Strata.png" alt="Strata Logo" width="120" />
  <h1>Strata Wallpaper</h1>
  <p><b>A lightweight, high-performance Windows 11 live wallpaper engine.</b></p>
  
  [![Windows 11](https://img.shields.io/badge/Windows-11-0078D6?style=flat&logo=windows11)](https://www.microsoft.com/windows/windows-11)
  [![C++](https://img.shields.io/badge/C++-00599C?style=flat&logo=c%2B%2B)](https://isocpp.org/)
  [![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=c-sharp)](https://learn.microsoft.com/en-us/dotnet/csharp/)
  [![WinUI 3](https://img.shields.io/badge/WinUI-3-00B4D8?style=flat)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
</div>

<br />

Strata seamlessly plays your favorite videos behind your desktop icons while maintaining a beautiful, modern interface to manage them. Built with a C++ `libmpv` backend for low GPU usage, and a WinUI 3 control panel for a premium feel.

## 📸 Screenshots

![Strata Wallpaper Control Panel](assets/screenshot.png)
*(The Strata Control Panel featuring the Dracula theme)*

---

## ✨ Features

- **Live Desktop:** Plays videos directly behind your desktop icons using WorkerW embedding.
- **Multi-Monitor Support:** Copy, Stretch, and Individual display modes across all your screens.
- **Smart Pause:** Automatically freezes playback when a fullscreen game is open, when you're on battery saver, during RDP sessions, or when locked.
- **Lock Screen Overlay:** A customizable screensaver-style overlay with password protection and a live clock.
- **Day/Night System:** Automatically swaps wallpapers based on Sunrise/Sunset or Islamic Prayer Times.
- **Beautiful Themes:** Ships with Aura, Catppuccin, and Dracula themes (with Light/Dark modes).
- **Performance Focused:** Adjust FPS limits, quality presets, and GPU scalers to save resources.

---

## 📥 Installation (For Users)

1. Go to the [**Releases**](https://github.com/Nobodyssssy/Strata-Wallpaper/releases) page.
2. Download the latest **`StrataWallpaper_Setup.exe`**.
3. Run the installer and follow the prompts.
4. Launch **Strata Wallpaper** from your Start Menu or Desktop shortcut.

> **Note:** Windows 11 is required. All dependencies (including the `libmpv-2.dll` video backend) are bundled inside the installer.

---

## 🛠️ Building from Source (For Developers)

### Prerequisites
- **Visual Studio 2022** (with C++ Desktop Development and .NET Desktop Development workloads).
- **Windows 11 SDK** (10.0.19041.0 or newer).
- **`libmpv-2.dll`** (This file is 154MB and is excluded from the repository due to GitHub limits. You must download it from the [mpv project](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/) and place it in `libs\mpv\`).

### Steps
1. Clone the repository:
   ```bash
   git clone https://github.com/Nobodyssssy/Strata-Wallpaper.git