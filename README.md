# GeniaFolder

GeniaFolder is a small Windows utility for managing useful folders without replacing Explorer.

## 0.1.2 scope

- add an existing Windows folder;
- create a new folder;
- assign a visible color/icon in Explorer;
- open folders quickly;
- keep a lightweight local registry;
- remove a folder from GeniaFolder without deleting user data.

Password protection is deliberately not faked in 0.1. It will be introduced only with authenticated encryption and a recovery design.

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

The managed folder itself receives hidden `.geniafolder.ico` and `desktop.ini` files so Windows Explorer can display the assigned color. Legacy `.geniafolder/folder.ico` data is migrated away when it is safe to do so.

## 0.1.2 fixes

- Repeated color changes now reset hidden/system attributes before rewriting `desktop.ini`.
- Folder color metadata uses hidden `.geniafolder.ico` instead of a visible `.geniafolder` directory.
- Legacy `.geniafolder/folder.ico` is removed automatically when safe.
- `Создать папку` now opens a GeniaFolder create dialog first; the system folder picker is only opened by `Обзор…`.
