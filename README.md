# GeniaFolder

Windows folder manager with colored Explorer folders, tray support, single-instance control, and secure encrypted vaults in development.

## Current version: 0.2.0-alpha.9

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


## 0.2.0-alpha.2 verified encrypted vault

This milestone adds real content encryption without deleting plaintext source data:

- a separate hidden sibling vault is created next to the managed folder;
- file names and directory layout are stored only inside an encrypted manifest;
- file contents are encrypted in 1 MiB chunks with AES-256-GCM;
- each chunk uses a unique nonce and authenticated associated data bound to profile, folder, file and chunk metadata;
- reparse points/symlinks are rejected rather than followed;
- GeniaFolder-generated `desktop.ini` and folder-color icon metadata are excluded from user-data encryption;
- source files are opened read-only and checked for changes during migration;
- after encryption, every file is fully decrypted in memory and checked against its plaintext SHA-256;
- the encrypted manifest is authenticated and its ciphertext hash is recorded in the protection profile;
- staging data is written to a temporary directory and renamed only after full verification;
- cancellation or failure cleans the staging copy and never modifies source files.

A verified vault is still a **copy** in alpha.2. Plaintext activation/removal is intentionally deferred until restore testing is implemented.


## 0.2.0-alpha.3 verified restore

The verified encrypted vault can now be restored into a new, separate folder:

- the user chooses the parent directory for the restored copy;
- the FEK is unlocked from the folder password only in memory;
- the encrypted manifest is authenticated before any restore output is trusted;
- all manifest paths are validated to prevent rooted paths, `..` traversal, invalid segments and case-insensitive path collisions;
- every encrypted chunk is authenticated with AES-256-GCM before plaintext is written;
- restored bytes are hashed while decrypting and must match the SHA-256 stored when the vault was created;
- restore output is written into a staging directory and published with a directory rename only after the complete restore succeeds;
- cancellation/failure cleans the staging restore and leaves the original folder and encrypted vault unchanged.

Plaintext removal is still disabled. The next security milestone should exercise recovery-key restoration and repeated crash/restore tests before activation of a vault-only mode.


## 0.2.0-alpha.4 Master Recovery restore

The restore flow now supports two independent FEK unlock paths:

- the normal folder password;
- the offline paper Master Recovery Key (`GF1-...`).

Both paths unlock the same random FEK and then feed the exact same authenticated restore pipeline. The recovery secret itself is not stored in plaintext by GeniaFolder. An invalid or unrelated recovery key fails before restoration begins.

This milestone verifies that losing the folder password does not make Standard-mode vault data unrecoverable as long as the paper Recovery Key is available.


## 0.2.0-alpha.5 security gate

A package-free security smoke-test runner is now part of the Windows CI pipeline. Every push builds the complete solution and exercises disposable vaults before CI can pass.

The smoke suite verifies:

- a wrong password cannot unlock the FEK;
- a modified/wrong Master Recovery Key cannot unlock the FEK;
- a normal password restore matches source files byte-for-byte;
- a Master Recovery restore matches source files byte-for-byte;
- a corrupted encrypted manifest is rejected;
- a corrupted encrypted file is rejected;
- a cancelled restore does not publish plaintext and cleans staging output;
- orphan vault staging from a simulated hard kill is cleaned on the next attempt.

These tests run only in temporary directories and never touch user vaults.


## 0.2.0-alpha.6 Vault-only

A verified Standard vault can now be promoted into a real vault-only state.

Before plaintext removal GeniaFolder:

- unlocks the FEK with the folder password;
- re-verifies the encrypted vault;
- re-reads the current plaintext tree and requires its file/directory set to match the encrypted manifest;
- hashes every plaintext file and requires its SHA-256 to match the vault manifest;
- refuses Vault-only if any file was added, removed, resized or changed after the vault was created.

The destructive transition is crash-aware:

- the protection profile enters `LockPending` before the source directory is atomically renamed into a sibling quarantine;
- the quarantine is removed only after the verified match;
- successful completion commits `VaultOnly`;
- a hard kill can be resumed from `LockPending` without guessing what happened;
- unlock restores through the same authenticated restore path and then returns the profile to plaintext-present state;
- an interrupted unlock that already published plaintext can be repaired by byte-for-byte verification rather than overwriting data.

GeniaFolder does not claim physical secure erase of SSD/NVMe blocks. Vault-only removes plaintext from the live filesystem namespace; storage-media remanence is outside this alpha milestone.


## 0.2.0-alpha.7 folder identity tracking

Managed folders now store the Windows filesystem identity of the directory (volume serial + file ID). If a plaintext folder is renamed in Explorer within the same parent directory, GeniaFolder can recognize the same directory and update its saved name/path automatically instead of treating it as deleted.

Legacy entries migrate automatically the next time their folder is available. Vault-only and LockPending states deliberately skip rename recovery because a missing plaintext path is expected there.


## 0.2.0-alpha.8 moved-folder recovery

Folder identity tracking now handles more than same-parent renames:

- if a missing plaintext folder was moved directly inside another folder already managed by GeniaFolder, the app can resolve it automatically by Windows volume serial + file ID;
- missing plaintext cards with a stored identity show a `Найти…` action;
- manual location is accepted only when the selected directory has the exact same Windows identity;
- look-alike folders, copies and cross-volume moves are not silently rebound;
- Vault-only and LockPending still disable plaintext relocation recovery because a missing plaintext path is expected in those states.


## 0.2.0-alpha.9 relocation and protected removal

Cross-volume moves can change the Windows filesystem identity even when the user considers the folder to be the same data. GeniaFolder no longer trusts a reappeared path when its stored volume/file ID changed.

For a protected folder with a verified vault, `Найти…` can validate a relocation candidate by unlocking the profile with the folder password or Master Recovery Key and comparing the complete directory/file set and SHA-256 of every plaintext file against the authenticated vault manifest. Only a complete match updates the managed path and captures the new Windows identity.

Removing a protected entry from the GeniaFolder list now requires the folder password. Vault-only and LockPending entries cannot be removed from the list until the storage transaction is returned to plaintext-present state. Removing an entry does not delete the folder, protection profile or encrypted vault.

For repeatable manual testing, `RESET_TEST_ENV.cmd` safely moves the current test root and local GeniaFolder app state into timestamped backup folders, then creates a fresh `D:\GeniaFolder test\Папка 1`, `Папка 2` and `Папка 3`.
