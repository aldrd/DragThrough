# DragThrough

**Drag files onto any window — even the ones hidden behind File Explorer.**

DragThrough is a small Windows utility that gets File Explorer out of your way while you drag. When you pick up files in Explorer and the window you want to drop them on is hidden behind it, DragThrough temporarily hides Explorer so you can drop straight onto the window that was behind it. It also adds an optional **secondary taskbar** with smooth, draggable task buttons that is aware of your virtual desktops.

<!-- Tip: add a screenshot here, e.g. ![DragThrough](docs/screenshot.png) -->

[![Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Get%20the%20app-0078D6?style=for-the-badge&logo=microsoftstore&logoColor=white)](https://apps.microsoft.com/detail/9P08B85PZRF3)
[![Download](https://img.shields.io/badge/Download-DragThrough%20Setup-2ea44f?style=for-the-badge&logo=windows)](https://github.com/aldrd/DragThrough/releases/latest/download/DragThrough-Setup.exe)

[![Latest release](https://img.shields.io/github/v/release/aldrd/DragThrough?style=flat-square)](https://github.com/aldrd/DragThrough/releases/latest)
[![License](https://img.shields.io/badge/License-CC%20BY--ND%204.0-blue?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square&logo=windows)

## Download

Two ways to get it — same app, pick whichever you prefer.

**➡️ [Microsoft Store](https://apps.microsoft.com/detail/9P08B85PZRF3)**

Installs in one click and updates through the Store along with everything else.

**➡️ [Download the installer (DragThrough-Setup.exe)](https://github.com/aldrd/DragThrough/releases/latest/download/DragThrough-Setup.exe)**

A per-user install — no administrator rights, no UAC prompt. This build updates itself: it checks for
new versions and installs them with one click from the tray menu.

Either way you're done after one step — the app lives in the system tray. No .NET runtime needs to be
installed separately; everything ships in the single package.

## Features

### Drag through Explorer
- **Hold Shift (or the Windows key) while dragging** files in File Explorer, and Explorer slides out of the way so you can drop onto the window that was behind it.
- **Auto-minimize Explorer** after a successful drop, so your workspace stays tidy.

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

The secondary taskbar and window management are built on [ManagedShell](https://github.com/cairoshell/ManagedShell), the shell library behind [RetroBar](https://github.com/dremin/RetroBar). A vendored copy lives in this repository, under its own Apache 2.0 license.

## Support

If DragThrough saves you some clicks, you can [buy me a coffee](https://buymeacoffee.com/redozubov) ☕

## License

Copyright © 2026 Alexey Redozubov, Valery Pogorelov.

DragThrough is licensed under [Creative Commons Attribution-NoDerivatives 4.0 International](LICENSE) (CC BY-ND 4.0).

**You may**, free of charge and without asking permission:

- use the app for anything, personal or commercial, at home or inside a company;
- redistribute it, including for a fee — mirrors, download sites and resellers are welcome;
- keep the copyright, license and attribution notices intact when you do.

**You may not** publish a modified version. You can change your own copy however you like; what the
license withholds is the right to *share* the result. Pull requests therefore cannot be accepted
without a separate contributor agreement — please open an issue instead, feature ideas are welcome.

This summary is for orientation only — the binding terms are those in [LICENSE](LICENSE).

### Scope

The license above covers the DragThrough application authored by the copyright holders named
above: its source code, resources and compiled binaries.

It does **not** cover third-party components distributed with this software. Those remain under
their own licenses, which in several cases grant broader rights — including the right to modify
them. ManagedShell, for instance, is Apache 2.0 and may be modified freely. Nothing in
DragThrough's license restricts the rights you receive under theirs. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full list.
