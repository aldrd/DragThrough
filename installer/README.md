# DragThrough installer

A per-user Windows installer built with [Inno Setup 6](https://jrsoftware.org/isdl.php).

## What it does

- Installs into `%LocalAppData%\Programs\DragThrough` — **no administrator rights / UAC prompt**.
  This matches the app, which runs as the signed-in user and self-updates by replacing its
  own `.exe` (only possible in a writable folder).
- Adds a Start Menu shortcut (and an optional desktop shortcut).
- **Autostart at sign-in** via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  (checkbox, on by default). Because the app runs as `asInvoker`, it starts silently with no UAC.
- Closes a running instance before upgrading, and removes the autostart entry on uninstall.

> Note: DragThrough is a desktop GUI app (taskbar + tray), so it is **not** a Windows service.
> A service runs in the isolated Session 0 and cannot draw a taskbar/tray/UI. "Start with
> Windows" for this kind of app is correctly done with login autostart, which is what this
> installer sets up.

## Build

Requires the .NET SDK and Inno Setup 6 (`ISCC.exe` on PATH or in its default folder).

```powershell
pwsh installer\build.ps1                 # version taken from ZombieBar.csproj <FileVersion>
pwsh installer\build.ps1 -Version 1.2.3.0
```

The script publishes a self-contained single-file build and compiles the installer to
`installer\Output\DragThrough-Setup-<version>.exe`.

## Manual build

```powershell
dotnet publish ZombieBar\ZombieBar.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none

ISCC.exe /DMyAppVersion=1.2.3.0 `
    "/DPublishDir=ZombieBar\bin\Release\net10.0-windows\win-x64\publish" `
    installer\DragThrough.iss
```
