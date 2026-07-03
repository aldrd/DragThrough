# DragThrough

**Drag files onto any window — even the ones hidden behind File Explorer.**

DragThrough is a small Windows utility that gets File Explorer out of your way while you drag. When you pick up files in Explorer and the window you want to drop them on is hidden behind it, DragThrough temporarily hides Explorer so you can drop straight onto the window that was behind it. It also adds an optional **secondary taskbar** with smooth, draggable task buttons that is aware of your virtual desktops.

<!-- Tip: add a screenshot here, e.g. ![DragThrough](docs/screenshot.png) -->

[![Download](https://img.shields.io/badge/Download-DragThrough%20Setup-2ea44f?style=for-the-badge&logo=windows)](https://github.com/aldrd/DragThrough/releases/latest/download/DragThrough-Setup.exe)
[![Latest release](https://img.shields.io/github/v/release/aldrd/DragThrough?style=flat-square)](https://github.com/aldrd/DragThrough/releases/latest)
[![License](https://img.shields.io/badge/License-Apache%202.0-blue?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square&logo=windows)

## Download

**➡️ [Download the installer (DragThrough-Setup.exe)](https://github.com/aldrd/DragThrough/releases/latest/download/DragThrough-Setup.exe)**

Run the installer and you're done — the app lives in the system tray and updates itself automatically. No .NET runtime needs to be installed separately; everything ships in the single package.

## Features

### Drag through Explorer
- **Hold Shift (or the Windows key) while dragging** files in File Explorer, and Explorer slides out of the way so you can drop onto the window that was behind it.
- **Auto-minimize Explorer** after a successful drop, so your workspace stays tidy.
- Press **Escape** to dismiss Windows Search instantly.
- Each option can be toggled from the tray menu; the modifier keys are configurable.

### Secondary taskbar
- A slim, optional extra taskbar with **draggable task buttons** — reorder them by dragging, Chrome‑tab style.
- **Per–virtual‑desktop:** it shows only the windows that live on the current desktop, and can be shown or hidden independently on each desktop.
- **Close buttons** on wide enough task buttons, styled like the main taskbar.
- **Centered tasks:** when the bar isn't full, buttons are centered instead of left‑aligned (optional).

### Everything else
- Lives quietly in the **system tray**; nothing to keep open.
- **Easy installation of updates** — new versions are installed with one click.
- **Localized** into English, Русский, Español, Français, Português and 中文 (简体).

## Requirements

- Windows 10 or Windows 11 (64‑bit)

## Usage

1. Install and launch DragThrough — a tray icon appears.
2. Start dragging files in File Explorer and hold **Shift** (or the **Windows key**): Explorer hides so you can drop onto the window behind it.
3. Right‑click the tray icon to toggle features (drag modifiers, auto‑minimize, the secondary taskbar, task centering) and to access About / updates.

## Building from source

DragThrough is a .NET 10 WPF application.

```bash
git clone https://github.com/aldrd/DragThrough.git
cd DragThrough
dotnet build ZombieBar/ZombieBar.csproj -c Release
```

The whole app builds into a single self‑contained executable.

## Credits

The secondary taskbar and window management are built on [ManagedShell](https://github.com/cairoshell/ManagedShell), the shell library behind [RetroBar](https://github.com/dremin/RetroBar). A vendored copy lives in this repository.

## Support

If DragThrough saves you some clicks, you can [buy me a coffee](https://buymeacoffee.com/redozubov) ☕

## License

Licensed under the [Apache License 2.0](LICENSE).
