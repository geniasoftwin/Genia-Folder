# GeniaFolder

Windows folder manager with colored Explorer folders, tray support, single-instance control, and secure encrypted vaults in development.

## Current version: 0.2.0-alpha.1

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
- single-instance protection: starting GeniaFolder again restores the already-running instance instead of creating a duplicate;\n- foreground handoff: a second user-initiated launch grants the running instance permission to restore and raise its existing window.

0.2.0-alpha.1 introduces the cryptographic key foundation for Standard Protection: a random 256-bit Folder Encryption Key (FEK), PBKDF2-HMAC-SHA256 password wrapping, AES-256-GCM authenticated key wrapping, and a high-entropy paper Master Recovery Key. The folder contents are deliberately still shown as **not encrypted** until the file-encryption layer is enabled.

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


## 0.2.0-alpha.1 security milestone

- random 256-bit FEK per protection profile;
- password-derived KEK using PBKDF2-HMAC-SHA256 with 600,000 iterations and a random salt;
- FEK wrapping with AES-256-GCM;
- independent 256-bit Master Recovery secret with checksum-protected GF1 recovery code;
- recovery secret is displayed before profile persistence and is never stored in plaintext;
- profile metadata is written atomically under `%LOCALAPPDATA%\GeniaFolder\protection`;
- both password and recovery routes are cryptographically verified before the profile can be saved;
- sensitive byte buffers are cleared with `CryptographicOperations.ZeroMemory` where practical.

This milestone prepares keys only. Existing user files are not modified or deleted.
