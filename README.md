# GeniaFolder

GeniaFolder is a small Windows utility for managing useful folders without replacing Explorer.

## Current version: 0.1.6

Implemented:

- add an existing Windows folder;
- create a new folder;
- assign a visible color/icon in Explorer;
- immediate Explorer refresh when the color changes;
- open folders quickly;
- track folders that are deleted, disconnected or restored;
- remove a folder from GeniaFolder without deleting user data;
- minimize normally to the Windows taskbar;
- close with **X** to keep GeniaFolder running in the notification area;
- double-click the tray icon to restore the window;
- exit the process explicitly from the tray menu;
- single-instance protection: starting GeniaFolder again restores the already-running instance instead of creating a duplicate.

Password protection is deliberately not faked. It will be introduced only with authenticated encryption, Master Recovery and a crash/force-kill-safe lifecycle.

## Build

Requirements:

- Visual Studio 2026 with **.NET desktop development**;
- .NET 10 SDK.

Open `GeniaFolder.slnx` or run:

```powershell
dotnet build src\GeniaFolder\GeniaFolder.csproj -c Release
```

## Storage

GeniaFolder metadata is stored in:

`%LOCALAPPDATA%\GeniaFolder\folders.json`

Managed folders receive hidden per-color icon files such as `.geniafolder-blue.ico` plus `desktop.ini`, allowing Windows Explorer to show the selected color while avoiding stale icon-cache reuse.

## Lifecycle policy

- **Minimize (—):** normal taskbar minimize.
- **Close (X):** hide the window and continue in the tray.
- **Tray → Exit:** terminate GeniaFolder.
- **Second launch:** activate the existing instance.
- **Force kill/crash:** the operating system releases the single-instance mutex automatically; the next launch is allowed.

The future protection layer must not depend on graceful process shutdown for data security.
