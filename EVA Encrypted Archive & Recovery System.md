# EVA  Encrypted Archive & Recovery System

## Functional Specification — Version 1

# 1. Purpose

EVA is a Windows 11 background application (with some limited front end interactions) designed to provide automatic, versioned, encrypted backups of a configured directory.

The primary use case is protecting a relatively small collection of working files such as Markdown, SVG, PNG and other documents.

EVA periodically checks the source directory for changes. When changes are detected, it creates an encrypted incremental archive containing the changes since the previous archive.

At configured intervals, EVA creates a complete snapshot of the source directory. Snapshots provide independent restore points and act as reset points for incremental archive chains.

EVA writes encrypted `.eva` files to a configured primary archive directory and optionally copies them to a second configured directory, such as a removable SSD.

Cloud synchronisation is deliberately outside the scope of EVA. Applications such as OneDrive can monitor the primary archive directory and synchronise the encrypted archives to cloud storage.

The application must also support restoring the state of the source directory as it existed at a particular point in time.

Not in the initial version, but ultimately a series of CLI commands will be made available.

# 2. Design Principles
The system should prioritise:
1. Data safety
2. Reliable recovery
3. Strong encryption
4. Self-describing archives
5. Simple operation
6. Provider independence
7. Human-readable archive organisation
8. Minimal resource usage
9. Safe failure behaviour
10. Portability of archives

The application must favour preservation of data over successful completion of a backup operation.

A failed backup must never result in destruction or modification of source files.

## Archive independence
Every `.eva` file must be independently cryptographically verifiable, authenticated, decrypted, decompressed and parsed without requiring an external database or other EVA installation state.

An incremental archive does not contain sufficient information to reconstruct the complete source independently; its dependency on its parent chain is therefore limited to reconstruction of the source state. But it should still be independently:
* authenticated
* decrypted
* decompressed
* parsed
* verified
without needing an external database.

# 3. Platform
Initial target platform:

- Windows 10/11
- Desktop application
- Background operation
- Windows system-tray notification-area icon
- Optional automatic startup with Windows
- .NET/C#

The application must not require a continuously running server or external service.

# 4. Terminology

### Source
The directory being protected.

Example:
```text
D:\Mardown_Documents\
```

### Primary destination
The directory into which EVA writes encrypted archives.

Example:
```text
D:\OneDrive\EVA\
```
It is assumed that this directory is monitored by external cloud-sync software.

### Secondary destination
An optional second directory to which EVA copies successfully created archives.

This is intended for removable/offline storage such as an external SSD.

Example:
```text
E:\ExternalStorage_Backup\
```

### Snapshot
A complete representation of the source directory at a particular point in time.

A snapshot contains everything required to reconstruct the source directory without requiring earlier archives.

### Incremental archive
An archive containing only changes since its immediate parent archive.

Changes include:
- New files
- Modified files
- Deleted files

An incremental archive depends on its parent chain for complete restoration.

### Archive chain
A snapshot followed by zero or more incremental archives.

Example:
```text
Snapshot
   ↓
Incremental
   ↓
Incremental
   ↓
Incremental
```
A new snapshot starts a new archive chain.

### Restore point
A specific archive representing the state of the source directory at a particular point in time.

# 5. Archive File Naming
Archive files use the `.eva` extension.

The filename format is:
```text
YYYY_MM_DD_HHMMSS_full_##.eva
YYYY_MM_DD_HHMMSS_incremental_##.eva
```

Example:
```text
2026_08_29_143000_full_01.eva
```

Where:
- `YYYY` = four-digit year
- `MM` = month
- `DD` = day
- `HHMMSS` = hour/minute/second
- `##` = sequence number

The sequence number prevents filename collisions when multiple archives are created within the same timestamp resolution.

Archives must sort chronologically when sorted lexicographically by filename.

The filename identifies the archive but does not determine whether it is a snapshot or incremental archive. Archive type is recorded in the archive metadata.


# 6. EVA Archive Format
`.eva` is a custom archive format.

The format should contain:

1. A small unencrypted EVA header.
2. An encrypted payload.

Conceptually:
```text
┌──────────────────────────────┐
│ EVA HEADER                   │
│                              │
│ Format version               │
│ Archive ID                   │
│ Archive type                 │
│ Chain ID                     │
│ Parent ID                    │
│ Creation time                │
│ KDF parameters               │
│ Salt                         │
│ GCM nonce                    │
│ Encryption algorithm         │
├──────────────────────────────┤
│ ENCRYPTED PAYLOAD            │
│                              │
│ compressed manifest + files  │
├──────────────────────────────┤
│ AUTHENTICATION TAG           │
└──────────────────────────────┘
```

The EVA format must be versioned.

The authentication tag is stored outside the authenticated header and is not itself included in the AAD.

The exact serialized bytes of the EVA header must be supplied to AES-256-GCM as Additional Authenticated Data. The header must not be reconstructed or semantically re-serialized before authentication.

The unencrypted header should contain only information required to identify and manage the archive.

It must not contain:
- Source directory paths
- File names
- File contents
- Other unnecessary information about the user's data

The purpose of the unencrypted header is to allow EVA to discover and organise archives without decrypting every archive simply to determine its identity and dependency relationships.

# 7. Compression
The archive payload should use lossless compression before encryption.

ZIP is the preferred initial implementation because:

- It is mature.
- It is widely supported.
- It is free.
- It provides good compression for text-based files.
- It supports the expected file types.

However, ZIP is an implementation detail rather than a requirement of the EVA format.

The EVA specification must not depend on ZIP-specific encryption.

The processing pipeline should be:
```text
                 ┌── File metadata
Source files ────┼── Manifest
                 │
                 └── File contents
                         ↓
                   Compression
                         ↓
                    Encryption
                         ↓
              ┌─────────────────────┐
              │ EVA Header          │
              ├─────────────────────┤
              │ Encrypted Payload   │
              └─────────────────────┘
                         ↓
                      .eva
```
Encrypted data must not be compressed after encryption.

# 8. Encryption
EVA must use modern authenticated encryption.

The algorithm that must be used is:
```text
AES-256-GCM
```
Encryption must provide:
* Confidentiality
* Integrity protection
* Cryptographic authentication of the encrypted data

ZIP's built-in encryption/password functionality must NOT be used.

The complete compressed payload, including the manifest, must be encrypted.

The application must never write an unencrypted archive to the configured archive destinations.

The approach is:
```
Source
 ↓
ZIP/compression stream
 ↓
AES-GCM streaming encryption
 ↓
temporary .tmp EVA
 ↓
verify
 ↓
rename
```
If implementation constraints make entirely in-memory processing undesirable, temporary files may be used, but they must be stored in an appropriate private temporary location, must not be exposed as valid `.eva` archives, and must be deleted after successful completion. The preferred implementation should stream compression directly into encryption to avoid creating a complete unencrypted intermediate archive on disk.

# 9. Encryption Key and Password
The user must establish a secret used to protect EVA archives.

The plaintext password must never be stored in the normal configuration file.

EVA Version 1 must use **Argon2id** as its password-based key-derivation function. The software must select appropriate Argon2id parameters for the target system. The parameters must be recorded in the archive. Advanced configuration of KDF parameters is outside the scope of Version 1.

A cryptographically secure random salt must be generated for each archive. The salt is not secret and must be stored in the EVA header.

EVA Version 1 must use **AES-256-GCM** for authenticated encryption.

Each archive must have a unique cryptographically secure random nonce/IV. The nonce is not secret and must be stored in the EVA header.

Each archive must contain the non-secret information required to decrypt itself, including:

* KDF identifier
* KDF parameters
* Random salt
* Encryption algorithm identifier
* Random nonce/IV
* Authentication tag

The password is processed as follows:
```text
Password
    │
    ▼
Argon2id
    │
    │  + unique per-archive salt
    │  + KDF parameters
    ▼
256-bit AES key
    │
    ▼
AES-256-GCM
    │
    ├── unique random nonce
    ├── EVA Header as authenticated data (AAD)
    └── compressed payload
            │
            ▼
    Ciphertext + authentication tag
```

The EVA header must be authenticated as Additional Authenticated Data (AAD). This allows the header to remain unencrypted while ensuring that unauthorised modification can be detected.

The encryption key, password, and other secret key material must never be stored inside the archive.

EVA archives must be decryptable using the user's password and information contained within the archive, without requiring Windows-specific authentication or services.

Windows-specific secret storage mechanisms such as **DPAPI** or Windows Credential Manager may be used to securely store credentials or derived key material locally for unattended operation. Such mechanisms are a convenience for the local EVA installation and must never be required to decrypt an EVA archive on another computer or operating system.

The encryption implementation must use a well-tested cryptographic library. EVA must not implement AES, GCM, Argon2id, or other cryptographic primitives itself.

EVA must not act upon decrypted archive contents as trusted data until AES-GCM authentication has successfully completed. Streaming decryption may be used internally, but decrypted data must be treated as untrusted until authentication succeeds.

# 10. Public EVA Header
The EVA header is intentionally unencrypted. It only: 
- contains metadata necessary for archive management. 
- must not contain file paths or file names.
- is authenticated as part of the encryption scheme so that it cannot be silently modified without detection.

Conceptually:
```text
EVA
formatVersion: 1
archiveId: 2026_08_29_1430_01
archiveType: incremental
chainId: 2026_08_29_1200_01
parentArchiveId: 2026_08_29_1415_01
created: 2026-08-29T14:30:00+10:00
encryption: AES-256-GCM
```

# 11. Encrypted Manifest
The manifest is stored inside the encrypted payload.

The manifest describes the contents and relationships of the archive and contains the information required to reconstruct a restore point.

The manifest is represented as JSON.

It should contain at least:
- Format version
- Archive ID, must be unique within an archive collection
- Creation timestamp
- Archive type
- Chain ID
- Parent Archive ID, where applicable
- Source identifier, a stable identifier for the configured backup source
- File information
- Directory information

The manifest is encrypted with the compressed file data.

The representation will be JSON:
```json
{
    "formatVersion": 1,
    "archiveId": "2026_08_29_1430_01",
    "created": "2026-08-29T14:30:00+10:00",
    "archiveType": "incremental",
    "chainId": "2026_08_29_1200_01",
    "parentArchiveId": "2026_08_29_1415_01",
    "files": [],
    "directories": []
}
```

## File Information

For each file represented by the archive, the manifest records: 
- Relative path
- Operation, for incremental archives
- File size
- Last modification timestamp
- Cryptographic hash
- Archive location, a reference identifying the file's content within the compressed archive payload. The exact representation is defined by the archive payload format.

For snapshots, the manifest represents the complete set of files present in the source directory.

For incremental archives, the manifest contains only the changes since the parent archive.

Possible incremental operations:
- added
- modified
- moved
- deleted

Version 1 does not require move detection. A file moved to a different path may be represented as a deletion at the old path and an addition at the new path. Implementations may detect and record moves as an optimisation, provided restore semantics remain identical.

## Directory Information
The manifest must record directories so that an exact restore can reconstruct the directory structure, including empty directories.

## Manifest and header consistency
The manifest repeats key archive identity fields from the public header. EVA must verify that these values agree when an archive is opened.

Deleted files are represented by manifest entries indicating deletion.

# 13. Archive and Chain Identity
Every archive has a unique:

### Archive ID
Identifies the individual archive.

Example:
```text
2026_08_29_1430_01
```

### Chain ID
Identifies the snapshot from which the archive chain originated.

Example:
```text
2026_08_29_1200_01
```

### Parent Archive ID
Identifies the immediately preceding archive required to apply this archive.

Example:
```text
2026_08_29_1415_01
```

This provides a structure such as:
```text
Snapshot
2026_08_29_1200_01
        │
        ▼
Incremental
2026_08_29_1215_01
        │
        ▼
Incremental
2026_08_29_1230_01
        │
        ▼
Incremental
2026_08_29_1245_01
```
The chain can therefore be reconstructed without relying on an external database.

# 14. Change Detection
EVA periodically inspects the source directory.

The backup interval is configurable.

Example:
```
Every 15 minutes
```
The application determines whether the source directory has changed since the previous successful archive.

Change detection must not rely exclusively on modification timestamps.

The application should use inexpensive metadata checks such as:
* Relative path
* File size
* Modification timestamp
to identify likely changes.

EVA may use path, size and modification timestamp as an efficient first-stage change detector. Where metadata indicates a possible change, EVA must verify the file contents using SHA-256 before determining whether a file is actually modified.

EVA must periodically perform full SHA-256 verification of tracked files, regardless of whether their metadata indicates a change, to detect changes that metadata-based detection may miss. The default full verification is due when the configured verification interval (default 24 hours) has elapsed since the last successful full verification. For example, if three days have passed since the last full verification, EVA performs one full verification, instead of attempting to perform one verification for each missed interval.

The application may maintain a local state/index to avoid unnecessarily hashing every file during every scan.

# 15. Files Being Written
EVA must avoid archiving files while they are actively being written.

When a change is detected:
1. Detect the change.
2. Wait briefly.
3. Check whether the file remains stable.
4. If the file is still changing, wait and retry.
5. Archive the file once it is stable.

If a file cannot be read successfully, EVA must not silently mark the file as successfully backed up.

The error must be recorded in the log.

# 16. Automatic Backup Behaviour
During normal operation:
1. The configured timer expires.
2. EVA scans the source directory.
3. EVA determines whether changes exist.
4. If no changes exist, no archive is created.
5. If changes exist, EVA creates an incremental archive unless a snapshot is due.
6. The resulting archive is encrypted.
7. The archive is written to the primary destination.
8. The archive is optionally copied to the secondary destination.
9. Retention rules are applied.

No archive should be created when nothing has changed.

Retention categories do not necessarily represent separate snapshot schedules.


# 17. Automatic Snapshots
EVA must periodically create complete snapshots.

The snapshot frequency is configurable.

Possible settings include:
- Daily
- Weekly
- Monthly

The default should be weekly.

A complete snapshot must contain the complete source directory at the time of creation.

A complete snapshot starts a new archive chain.

A complete snapshot should only be created when changes have occurred since the previous complete snapshot.

Example:
```text
Weekly snapshot
      │
      ▼
Incremental
      │
      ▼
Incremental
      │
      ▼
Incremental
      │
      ▼
Weekly snapshot
```
## Snapshot consistency

A snapshot represents the source directory as observed during the snapshot operation. EVA must apply the file stability checks defined in Section 15 to avoid archiving files while they are actively being written. If a file changes after it has been archived but before snapshot completion, EVA should detect the change and re-read the file before committing the snapshot. A snapshot must not be considered successful if EVA cannot establish a consistent final state for all files represented in its manifest.

# 18. Manual Snapshot
The user can manually initiate a full snapshot through the system-tray menu.

This operation must always create a complete snapshot, regardless of the normal automatic snapshot schedule.

This is intended for situations such as:

> "I am about to make major changes to my files. Create a snapshot now."

The manual snapshot starts a new archive chain.

Manual snapshots always create a new snapshot, the change requirement for a snapshot only applies to automatic snapshots. 

# 19. Archive Creation
When creating an archive:
1. Scan the source directory.
2. Determine changes.
3. Determine whether the archive is an incremental or snapshot archive.
4. Generate the manifest.
5. Package the manifest and required files.
6. Compress the package using the selected compression mechanism.
7. Construct and serialize the EVA header.
8. Compress the manifest and required files.
9. Encrypt the compressed payload using the serialized EVA header as AAD.
10. Write the encrypted archive to a temporary filename.
11. Flush and verify the archive.
12. Atomically rename the temporary file to its final `.eva` filename.
13. Copy it to the secondary destination if configured.
14. Verify the secondary copy.
15. Record the successful archive.
16. Apply retention rules.

The source directory must never be modified by the backup process.



# 20. Atomic Archive Creation

A new archive must initially be written using a temporary filename.

Example:
```text
2026_08_29_1430_01.tmp
```
After successful completion and verification:
```text
2026_08_29_1430_01.eva
```

The final `.eva` filename must not appear until the archive has been completely written and verified.

This prevents cloud synchronisation software from synchronising a partially written archive.

# 21. Multiple Destinations
EVA supports:

### Primary destination
Required.

This is the primary archive destination.

### Secondary destination
Optional.

This is intended primarily for removable/offline storage.

The secondary destination may be unavailable at any time.

If the secondary destination is unavailable:

- The primary archive should still be created.
- The failure must be reported.
- The archive should remain available for later copying.
- The failure must not invalidate the primary backup.

When the secondary destination becomes available again, the software should be able to copy any required archives that were not previously copied. The software must maintain sufficient local state to identify archives that have not yet been successfully copied to the secondary destination.

# 22. Cloud Storage

EVA must not directly integrate with cloud-storage providers.

The primary destination is simply a normal filesystem directory.

For example:
```text
EVA
 ↓
D:\OneDrive\EVA\
 ↓
OneDrive synchronisation
 ↓
Cloud
```

The cloud provider therefore receives only encrypted `.eva` files.

EVA does not need to know whether the destination is:

- OneDrive
- Dropbox
- Google Drive
- Network storage
- Local disk
- Other storage

# 23. Archive Discovery
EVA must be able to discover `.eva` archives in the configured destination.

The application must not rely exclusively on an external database to understand archive relationships.

The EVA headers provide enough information to establish:
- Archive identity
- Archive type
- Chain identity
- Parent relationship
- Creation time
- Format version

The application may maintain a local index/cache for performance.

The local index must not be the sole source of truth.

# 24. Chain Integrity
EVA must be able to detect incomplete archive chains.

Example:
```text
Snapshot
   ↓
I01
   ↓
I02
   ↓
I03
```

If `I02` is missing, EVA must identify the chain as incomplete.

It must not attempt a restore that would produce an incorrect representation of the source.

The user should receive an informative error such as:
```text
Archive chain incomplete.

Required archive:
2026_08_29_1300_01.eva
```

# 25. List Snapshot Content
The system-tray menu provides:

**List snapshot content**

Selecting this opens a window displaying available restore points.

The user can select an archive.

The application then displays the contents of that archive/restore point.

The content listing should include at least:
- Directory
- Filename
- Date modified

Example:
```text
Snapshot: 29 August 2026 14:30

Directory             Filename             Modified

Characters            Alice.md             29 Aug 14:21
Characters            Bob.md               28 Aug 19:42
History               Timeline.md          29 Aug 13:02
Images                Map.png              27 Aug 11:15
```

The interface should support navigating directories in a familiar tree/list structure.

For an incremental archive, "snapshot content" means the complete reconstructed content represented by that restore point, not merely the files physically contained in the individual incremental archive.


# 26. Restore Snapshot

The system-tray menu provides:

**Restore snapshot**

The user selects an available restore point.

EVA determines the archive chain required to reconstruct that restore point.

For example:
```text
Snapshot
   +
Incremental
   +
Incremental
   +
Incremental
```
The application decrypts and applies the required archives in chronological order.

# 27. Restore Destination
An exact restore means restoration of the directory structure, file contents, relative paths, file sizes and last-modified timestamps recorded by EVA. Windows-specific filesystem metadata not explicitly supported and is outside the scope of exact restoration.

The user is prompted to select a destination directory.

The source directory is displayed for reference.

Example:
```text
Restore snapshot

Restore point:
29 August 2026 14:30

Original:
D:\Worldbuilding\

Restore to:
[ D:\Restored\Worldbuilding\ ] [...]

                         [Cancel] [Restore]
```
By default, the destination must be different from the configured source directory.

Version 1 should **not support restoring directly over the original source directory**.

This restriction is intentional to minimise the possibility of accidental destructive restoration.

Restoring to a separate directory allows the user to inspect the restored content before manually replacing the original.

# 28. Restore Accuracy
The restored directory must represent the exact state of the source directory at the selected restore point.

This means:

- Files existing at that time must exist.
- Files modified before that time must contain the appropriate historical version.
- Files created after that time must not exist.
- Files deleted before that time must not exist.
- Directory structure must match the historical state. This includes empty directories.

Where practical, restored file hashes should be verified against the information recorded in the archive manifest.


# 29. System Tray Interface
EVA runs primarily as a background application.

The system tray context menu should contain exactly these primary actions:

```text
EVA
────────────────────────────
Create snapshot now
List snapshot content
Restore snapshot
────────────────────────────
Options
────────────────────────────
Exit
```

The tray menu is intended for launching operations and configuration.

The primary user interface is a window launched from these menu options.

# 30. Create Snapshot Now
Selecting:

**Create snapshot now**

immediately starts creation of a complete snapshot.

The application should provide visual feedback that the operation is in progress.

The operation should run asynchronously so that the user interface remains responsive.

When complete, the user should be informed of:

- Snapshot creation success/failure
- Archive filename
- Archive size
- Primary destination status
- Secondary destination status

Archive size refers to the size of the individual `.eva` archive. The UI may additionally display total chain size where useful.

# 31. List Snapshot Content Interface
Selecting:

**List snapshot content**

opens a window containing:

### Restore-point list
Showing:

- Date/time
- Archive type
- Archive size
- Chain status

The user selects a restore point.

### Content view
The selected restore point's reconstructed directory contents are displayed.

The user can navigate the directory structure and see:

- Directory
- Filename
- Date modified

The application must not need to restore files to disk simply to display the directory listing.

EVA should decrypt and process the manifests without extracting file contents to disk. File contents should only be decrypted when required for restoration or explicit future file-recovery functionality.

# 32. Restore Interface
Selecting:

**Restore snapshot**

opens a restore workflow.

The user:
1. Selects a restore point.
2. Views its date/time and status.
3. Selects a destination directory.
4. Confirms the restore.
5. EVA validates the required archive chain.
6. EVA decrypts and reconstructs the selected state.
7. EVA writes the restored files.
8. EVA verifies the restored files where practical.
9. EVA reports the result.

Restoration should not modify the source directory.


# 33. Options Interface
Selecting:

**Options**

opens the configuration window.

Configuration should be grouped logically.

### Backup
```text
Source directory:
[ D:\Worldbuilding\ ]

Backup interval:
[ 15 minutes ▼ ]

Snapshot frequency:
[ Weekly ▼ ]
```

### Destinations
```text
Primary archive directory:
[ D:\OneDrive\EVA\ ]

Secondary archive directory:
[ E:\EVA\ ]

☐ Enable secondary destination
```

### Retention
```text
Incremental archives:
[ 7 ] days

Weekly snapshots:
[ 1 ] year

Monthly snapshots:
[ Forever ▼ ]
```

### Security
```text
Encryption:
AES-256-GCM

[ Change encryption password... ]
```
Each archive is independently encrypted using a key derived from the password and that archive's unique KDF salt. Changing the password does not change existing archives. Existing archives remain decryptable using the password that was active when they were created. However, changing the password must start a new archive chain. EVA must create a complete snapshot using the new password before creating incremental archives using that password.

### Startup
```text
☑ Start EVA with Windows
```
The actual configuration UI layout is an implementation/design decision.

# 34. Configuration Validation
Before accepting configuration changes, EVA must validate:

- Source directory exists.
- Source directory is readable.
- Primary destination exists or can be created.
- Primary destination is writable.
- Secondary destination exists or can be created when enabled.
- Encryption configuration is valid.
- Retention settings are valid.
- Backup interval is valid.
- Snapshot frequency is valid.
- Source and destination paths do not create recursive backup relationships.

The application must prevent unsafe configurations such as placing the source directory inside the archive destination.

# 35. Retention Policy
The initial default retention policy is:
Note that retention categories do not necessarily represent separate snapshot schedules.

### Incremental archives
Keep:
```text
7 days
```

### Weekly snapshots
Keep:
```text
1 year
```

### Monthly snapshots
Keep:
```text
Forever
```
All values should be configurable.

# 36. Retention Rules
Retention must never delete an archive that is required to reconstruct a retained restore point.

When an archive is considered for deletion, EVA must evaluate archive dependencies.

The application should preferentially delete archives that are:

- Outside their configured retention period.
- Not required by any retained restore point.
- Not required as the parent of a retained archive.

When a new snapshot starts a new chain, older chains can be evaluated independently.

Retention periods are minimum/target retention periods. An archive may be retained beyond its configured retention period if it is required to reconstruct another retained restore point.

# 37. Monthly Snapshots
A monthly snapshot is a complete snapshot retained indefinitely.

A monthly snapshot should only be created when changes have occurred since the previous relevant snapshot.

The system should select an appropriate snapshot within each month according to the configured snapshot schedule.

Example:
```text
2026_06_01_1200_01.eva
2026_07_01_1200_01.eva
2026_08_01_1200_01.eva
```
The precise scheduling behaviour may be configurable.

# 38. Weekly Snapshots
Weekly snapshots are complete snapshots retained for the configured retention period.

Default:
```text
1 year
```
Weekly snapshots provide relatively short archive chains and convenient historical restore points.

A complete snapshot may simultaneously satisfy the weekly and monthly retention categories. The software should avoid creating duplicate snapshots solely because multiple retention schedules coincide.

# 39. Incremental Retention
Incremental archives are retained for the configured short-term period.

Default:
```text
7 days
```
Incremental archives may be deleted earlier if they are no longer required by any retained restore point.

The implementation must ensure that deleting an incremental archive does not break a restore point that the retention policy says must remain available.

# 40. Logging
EVA must maintain a local application log.

Events should include:

- Application startup
- Application shutdown
- Configuration changes
- Backup started
- Backup completed
- Backup failed
- Files detected as changed
- Snapshot created
- Incremental created
- Archive copied to primary destination
- Archive copied to secondary destination
- Secondary destination unavailable
- Archive verification failure
- Chain integrity problems
- Restore started
- Restore completed
- Restore failed
- Retention cleanup
- Encryption/decryption failures

The log must never contain encryption passwords or encryption keys.



# 41. Failure Behaviour

EVA must fail safely.

### Source file cannot be read

The application must report the problem.

It must not silently claim that the file was backed up.

### Archive creation fails

No incomplete `.eva` file should be presented as a valid archive.

### Encryption fails

No encrypted archive should be considered valid.

### Archive verification fails

The archive must not be committed to its final filename.

### Primary destination unavailable

The backup should be reported as failed.

The source directory must remain untouched.

### Secondary destination unavailable

The primary backup may still succeed.

The secondary failure must be reported and retried later.

### Application crashes

Temporary files must not be mistaken for valid archives.

The application should clean up stale temporary files on startup.

### Computer loses power

The source directory must remain unaffected.



# 42. Security Requirements

The application must:

- Never use ZIP password encryption.
- Use authenticated encryption.
- Use cryptographically secure random salts and nonces.
- Use a strong password-derived key where appropriate.
- Never store plaintext passwords in configuration.
- Never log passwords or encryption keys.
- Never put sensitive file contents in logs.
- Encrypt the archive manifest.
- Authenticate the unencrypted EVA header.
- Verify archive integrity before accepting an archive as valid.
- Verify restored file contents where practical.
- Never modify source files during backup.
- Never restore over the source directory in version 1.



# 43. Resource Usage

EVA is intended to run continuously in the background.

When idle, CPU and memory usage should be minimal.

The application should avoid unnecessarily hashing every file during every check.

Metadata should be used to identify likely changes before cryptographic hashing is performed.

The expected source directory size is generally tens of megabytes, but the application should not impose an artificial low size limit.

Archive creation and encryption should be capable of handling substantially larger directories than the initial expected use case.



# 44. Archive Portability

An EVA archive should be portable.

It should be possible to copy `.eva` files between:

- Different disks
- Different computers
- Cloud storage
- Removable SSDs

The archive must not depend on:

- OneDrive
- A specific computer
- A specific EVA installation
- An external database

An archive must contain sufficient metadata to identify itself and its relationship to other archives.



# 45. Archive Format Evolution

Every EVA archive must contain a format version.

Example:

```text
formatVersion: 1
```

Future versions must be able to determine how to interpret older archives.

The application should maintain backwards compatibility with older archive formats where practical.

If an archive uses an unsupported format, the application must report that it cannot read the archive rather than attempting to interpret it incorrectly.



# 46. Initial User Experience

On first launch:

1. EVA displays the Options window.
2. The user selects a source directory.
3. The user selects a primary archive directory.
4. The user optionally enables a secondary archive directory.
5. The user configures the backup interval.
6. The user configures the snapshot frequency.
7. The user configures retention.
8. The user establishes the encryption secret.
9. EVA validates the configuration.
10. EVA performs an initial complete snapshot.
11. EVA minimises to the system tray.
12. EVA begins normal background operation.



# 47. Normal Operational Model

```text
                    SOURCE
                       │
                       ▼
                 Periodic check
                       │
                       ▼
                 Changes found?
                  /          \
                No            Yes
                │              │
                │              ▼
                │        Snapshot due?
                │          /       \
                │        Yes        No
                │         │          │
                │         ▼          ▼
                │     SNAPSHOT   INCREMENTAL
                │         │          │
                │         └────┬─────┘
                │              │
                │              ▼
                │         Create manifest
                │              │
                │              ▼
                │          Compress
                │              │
                │              ▼
                │           Encrypt
                │              │
                │              ▼
                │        Verify archive
                │              │
                │              ▼
                │         Atomic rename
                │              │
                │       ┌──────┴──────┐
                │       ▼             ▼
                │   Primary       Secondary
                │   destination   destination
                │       │             │
                │       ▼             ▼
                │      Cloud        SSD
                │
                ▼
             Wait for
          next interval
```

# 48. Restore Model
A restore point is reconstructed from its archive chain.

Example:
```text
Snapshot
2026_08_29_1200_01
       │
       ├── 12:15 Incremental
       ├── 12:30 Incremental
       ├── 12:45 Incremental
       ├── 13:00 Incremental
       └── 13:15 Incremental
```

To restore the 13:15 state:
```text
Load Snapshot
       ↓
Apply 12:15
       ↓
Apply 12:30
       ↓
Apply 12:45
       ↓
Apply 13:00
       ↓
Apply 13:15
       ↓
Verify
       ↓
Write restored directory
```
The restore engine must process archive operations in chronological order.

EVA should perform restoration into a temporary directory within or alongside the selected restore destination where practical. A failed restore must not present a partially restored directory as a successfully completed restore.

# 49. Verification

### Structural verification
```
Is this a valid EVA?
Is the header valid?
Can the authentication tag be verified?
Can the payload be decompressed?
Is the manifest valid?
```

### Chain verification
```
Does parent exist?
Does parent belong to same chain?
Is parent valid?
Are there missing links?
```

### Content verification
```
Does decrypted file hash match manifest?
```

This provides future support for a CLI for commands like:
```
eva verify
eva verify <archive>
eva verify --chain <chain>
```

# 50. Desired Outcome
The finished application should provide confidence that:

> If the working directory is accidentally deleted, corrupted, modified incorrectly, or otherwise damaged, the user can select a historical restore point and reconstruct the directory exactly as it existed at that point in time.

The application should operate automatically enough that the user does not need to think about backups during normal operation.

The `.eva` files should be:

- Encrypted
- Authenticated
- Compressed
- Versioned
- Self-describing
- Portable
- Recoverable without an external database
- Suitable for cloud synchronisation
- Suitable for offline/removable storage

The application should provide a simple system-tray interface while keeping the detailed functionality inside conventional Windows application windows.