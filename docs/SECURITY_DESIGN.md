# GeniaFolder security design (planned)

## Rule 1: UI passwords are not security

A normal Windows folder stays readable through Explorer. Therefore GeniaFolder will never call a folder "protected" merely because the app asks for a password.

## Planned protected-folder model

Each protected folder receives a random 256-bit Folder Encryption Key (FEK). File contents are encrypted with authenticated encryption. The FEK is wrapped separately for:

1. the user's folder password;
2. the user's offline Master Recovery Key, when recovery is enabled.

The password itself is never stored.

## Recovery modes

- **Standard** — password + Master Recovery allowed.
- **Secure** — password + Master Recovery; failed attempts produce increasing delays.
- **Extreme** — explicitly opt-in; recovery can be disabled. Destruction means cryptographic key destruction rather than claiming that SSD blocks were physically overwritten.

## Master Recovery Card

First-run recovery will generate a high-entropy offline recovery secret intended for paper storage. GeniaFolder must not retain a plaintext copy of that secret.

## Extreme Mode safety

A simple "three wrong passwords destroys everything" policy can be abused by anyone who can access the machine. Therefore Extreme Mode must be off by default and require explicit acknowledgement. A separate Panic Password is preferred over accidental-trigger destruction.

No developer backdoor or universal recovery key is permitted.


## Implemented in 0.2.0-alpha.1

The first protection milestone implements the key hierarchy without touching user files:

- 256-bit random FEK;
- PBKDF2-HMAC-SHA256 password KEK (600,000 iterations, random salt);
- AES-256-GCM authenticated wrapping of the FEK;
- independent 256-bit Master Recovery secret;
- human-readable `GF1-` recovery code with a checksum for transcription-error detection;
- atomic profile persistence only after the user confirms the paper Recovery Key;
- pre-persistence round-trip verification through both password and recovery routes.

A prepared profile is **not** presented as an encrypted folder. File contents remain plaintext until the next protection milestone implements authenticated file encryption and crash-safe migration.


## Implemented in 0.2.0-alpha.2

The first real content-encryption milestone creates a separate verified vault while leaving source plaintext untouched.

Vault v1 properties:

- encrypted file names are random GUIDs; original names and directory layout live only in the encrypted manifest;
- file data is chunked at 1 MiB and protected with AES-256-GCM;
- each chunk has a unique nonce derived from a random per-file prefix plus chunk index;
- AEAD associated data binds chunks to profile ID, folder ID, file ID, chunk index, original file length and chunk length;
- the encrypted manifest is also protected by AES-256-GCM and bound to the profile/folder IDs;
- files are re-read through decryption after creation and SHA-256 checked before the vault is considered verified;
- reparse points are rejected to avoid traversal/cycle ambiguity;
- source files are never deleted or rewritten in this milestone;
- the final vault directory is published only after staging and verification complete.

The next milestone must implement restore/decryption testing before any plaintext source removal can be offered.


## Implemented in 0.2.0-alpha.3

Restore is now implemented as an isolated verification path:

- restoration always targets a new folder and never overwrites an existing path;
- the encrypted manifest is authenticated before its paths are used;
- manifest paths are constrained to the restore root and checked for traversal/collisions;
- each file is restored chunk-by-chunk only after AES-256-GCM authentication succeeds;
- SHA-256 of restored plaintext must match the hash recorded at vault creation;
- output is staged under a temporary restore directory and renamed into place only after every file passes;
- cancellation or failure removes the staging output and does not modify the source plaintext or vault.

This provides an end-to-end encrypt -> verify -> restore -> verify loop before any feature is allowed to remove plaintext source data.


## Implemented in 0.2.0-alpha.4

Standard Protection now has two tested unlock routes for restore:

1. password -> PBKDF2-HMAC-SHA256 KEK -> password-wrapped FEK;
2. paper Master Recovery Key -> recovery KEK -> recovery-wrapped FEK.

After either route unwraps the same FEK, restoration uses the same authenticated manifest, AES-256-GCM chunk verification and plaintext SHA-256 checks. A recovery-key failure stops before any restore output is trusted or published.


## Implemented in 0.2.0-alpha.5

Vault-only mode is gated behind automated negative-path testing. CI now runs an isolated security smoke suite with disposable data and profiles.

Required passing scenarios include wrong password rejection, wrong Recovery Key rejection, byte-identical restore through both unlock routes, encrypted manifest corruption rejection, encrypted file corruption rejection, cancellation cleanup, and stale staging cleanup after simulated process death.

A CI failure blocks the security milestone from being treated as releasable.


## Implemented in 0.2.0-alpha.6

Vault-only is now a transactional state rather than a UI label.

The lock transition requires both the encrypted vault and the current plaintext source to pass verification. Plaintext file paths, lengths and SHA-256 hashes must match the authenticated manifest before any destructive action is permitted.

Storage state is persisted as `PlaintextPresent`, `LockPending` or `VaultOnly`. The source directory is atomically renamed into a GUID-scoped sibling quarantine after the profile records `LockPending`. A process death can therefore be resumed deterministically: source present, quarantine present, or both absent each have an explicit recovery path; simultaneous source+quarantine is treated as an inconsistency and automatic deletion stops.

Unlock uses the existing authenticated restore pipeline. If the process dies after restored plaintext has been published but before the profile state is updated, GeniaFolder can verify the existing plaintext against the vault and complete the state transition without overwriting it.

CI now also rejects stale-vault deletion, verifies Vault-only plaintext removal, verifies byte-identical unlock, exercises marked restore-staging cleanup, and resumes a simulated LockPending hard-kill state.


## Installation-wide Master Recovery (alpha.10)

The Standard Protection recovery root is installation-wide rather than per-folder. The first protected folder creates a random 256-bit Master Recovery secret. Only a SHA-256 verifier/fingerprint is persisted; the GF1 paper key is shown once and must be stored off-computer.

Each folder remains cryptographically independent: it receives a random FEK and a separate password-derived KEK. Recovery uses a folder-specific KEK derived with HMAC-SHA256 from the common Master secret, a random per-profile recovery salt, and FolderId. AES-256-GCM wraps the folder FEK with associated data binding ProfileId, FolderId and mode.

Legacy profiles using the earlier per-folder recovery algorithm remain readable by their original per-folder Recovery Keys. Fresh alpha.10 test environments use the installation-wide model.


## Stable managed-folder marker (alpha.13)

Plaintext managed directories contain a non-secret hidden/system `.geniafolder.id` marker with `GeniaFolder.Folder.v1|<folderId>`. It is excluded from encrypted vault contents and is recreated after authenticated vault restore. The marker is a location/identity hint, not an authentication secret. For protected entries, a marker match with a changed Windows filesystem identity still requires password/Master Recovery authentication and vault content verification when a verified vault exists.

The registry also keeps a bounded history of confirmed folder paths. Resolution uses exact historical paths, the parents/ancestors of known paths, and parents of other managed folders without recursively scanning entire drives. Multiple marker matches are treated as ambiguous rather than guessed.

## First-run Master Recovery setup (alpha.13)

Master Recovery Key creation is independent of folder protection. A fresh application state generates the installation-wide 256-bit recovery secret before the main folder UI is shown and displays the GF1 paper key once for offline recording. Only verifier/fingerprint metadata is persisted. Individual folder protection never generates a new paper recovery key.
