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
