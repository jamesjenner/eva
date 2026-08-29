# EVA Version 1 Technical Design: Archive Format and Crypto Boundary

## 1) Scope of this design

This document defines the concrete design for EVA Version 1 archive format and the cryptographic boundary, using [EVA Encrypted Archive & Recovery System.md](EVA%20Encrypted%20Archive%20%26%20Recovery%20System.md) as the authoritative specification.

This is a design document for review before implementation. It does not contain production code.

The purpose is to reduce ambiguity so that `ArchiveWriter` and `ArchiveReader` can be implemented without making additional architectural assumptions.

---

## 2) Design review: implementation assumptions vs explicit specification requirements

Before defining the format, it is important to separate what the functional specification explicitly requires from what is a recommended implementation choice.

### 2.1 Requirements explicitly stated by the functional specification

These are mandatory and must not be converted into implementation assumptions:

- EVA archives are encrypted `.eva` files.
- The archive format is versioned.
- The archive contains an unencrypted public header and an encrypted payload.
- The exact serialized bytes of the public header are passed as AES-GCM additional authenticated data.
- The authentication tag is stored outside the header and is not included in the AAD.
- AES-256-GCM is mandatory.
- Argon2id is mandatory for password-based key derivation.
- A unique per-archive random salt is stored in the header.
- A unique per-archive random nonce is stored in the header.
- The public header may contain only metadata needed for archive management.
- The header must not contain source directory paths, file names, file contents, or other unnecessary user data.
- The encrypted payload contains the manifest and the file contents/data required to reconstruct state.
- The complete compressed payload must be encrypted; ZIP password encryption must not be used.
- The manifest is JSON and is stored inside the encrypted payload.
- A manifest repeats key archive identity fields from the public header and must be checked for consistency.
- Archive chain relationships are described by archive ID, parent archive ID, and chain ID.
- A new snapshot starts a new archive chain.
- Incremental archives depend on their parent chain.
- Archive discovery must work from archive metadata without an external database.
- Archive creation must be atomic.
- Archive validation must reject malformed, partial, truncated, or tampered archives.
- The app must fail safely.

### 2.2 Recommended implementation decisions

These are not stated as formal specification requirements but are standard and reasonable for a Version 1 implementation:

- Use UTF-8 for JSON manifest encoding.
- Use little-endian binary encoding for fixed-width integer fields in the header.
- Use a binary header with explicit length prefixes for variable-length strings.
- Use a single compressed payload stream containing manifest + file payload objects.
- Use ZIP as the internal compression mechanism, while keeping ZIP as an implementation detail rather than a visible external format requirement.
- Use a canonical JSON schema and stable field ordering within the manifest.
- Use a local metadata index/cache for efficiency, but never as the only source of truth.
- Use temporary files during archive creation, but keep them in a private temporary directory and never expose them as valid archives.

### 2.3 Open questions requiring approval

These are not resolved by the specification and must be explicitly approved before coding begins:

- Whether the header should use a fully fixed-width binary format or a length-prefixed binary format.
- Whether the payload should be a single compressed object or a sequence of compressed blocks with explicit indexes.
- Whether to retain a local metadata database/index for performance or keep all archive discovery metadata in the filesystem header scan.
- Whether selected file timestamps are preserved exactly or normalized to UTC for restore accuracy.
- Whether the application should support a “degraded” mode that allows listing incomplete or partially missing chains without full restore.

> The first two items are not contradictions in the spec; they are design details that must be selected. They should not be confused with specification requirements.

---

## 3) Summary of the archive format design

The Version 1 design is:

- A small unencrypted public header at the front of the file.
- A binary payload section containing a compressed manifest and file contents.
- An authentication tag appended after the encrypted payload.

Conceptually:

- file = header + encrypted payload + tag
- header is not encrypted
- payload is encrypted using AES-256-GCM
- payload is authenticated by the GCM tag
- header bytes are included as AAD for authentication

This design is consistent with the specification and keeps the archive independently verifiable with no architectural state required.

---

## 4) Required archive file layout

The file layout is:

1. Public unencrypted header
2. Encrypted payload
3. Authentication tag

The file format is therefore:

- `[PublicHeader][EncryptedPayload][AuthenticationTag]`

The public header is always stored at offset 0 and is never encrypted.

The encrypted payload length is determined by archive size and is not externally visible as a separate field in the header beyond a length value if the implementation chooses to include it.

The authentication tag is always the final bytes of the archive.

---

## 5) Exact structure of the unencrypted header

The specification requires the header to be small and contain only metadata needed for management and discovery. It must not contain source directory paths, file names, file contents, or other unnecessary information.

### 5.1 Required header fields

Required header fields for Version 1:

- magic
- formatVersion
- archiveId
- archiveType
- chainId
- parentArchiveId
- createdUtc
- sourceId
- kdfIdentifier
- kdfParameters
- salt
- encryptionAlgorithmIdentifier
- nonce
- payloadLength
- headerLength

Notes:

- `archiveId` is a unique archive identifier.
- `parentArchiveId` is present for incremental archives and may be null for snapshots.
- `sourceId` is a stable identifier for the configured source directory.
- `kdfIdentifier` identifies the KDF algorithm, which must be `Argon2id` for Version 1.
- `kdfParameters` stores the Argon2id configuration used to derive the key.
- `salt` is cryptographically random and unique per archive.
- `nonce` is cryptographically random and unique per archive.
- `payloadLength` is the byte length of the encrypted payload.
- `headerLength` is the byte length of the serialized public header, to support strict validation.

### 5.2 Required by the specification vs recommended format decision

Required by the specification:
- A public header exists.
- It contains archive identity and management metadata only.
- It contains KDF and encryption metadata.
- It must not expose source paths or file names.
- It includes format version, archive type, chain ID, parent ID, creation time, salt, nonce, and algorithm references.

Recommended implementation decision:
- Use a fixed binary header structure with explicit field lengths and little-endian encoding.
- Include `headerLength` and `payloadLength` to support robust validation and streaming.
- Use `sourceId` as a generated stable identifier rather than a filesystem path.

Open question requiring approval:
- Whether the archive ID and chain ID should be fixed-length timestamp-based strings or a more general opaque string form.

### 5.3 Recommended concrete header format

The following is the recommended Version 1 public-header layout.

Header fields are defined as follows:

- magic: 4 bytes, ASCII "EVA1"
- formatVersion: 2 bytes, unsigned 16-bit integer, value 1
- archiveType: 1 byte, enum
  - 0 = snapshot
  - 1 = incremental
- reserved: 1 byte, set to 0 for Version 1
- headerLength: 4 bytes, unsigned 32-bit integer
- payloadLength: 8 bytes, unsigned 64-bit integer
- archiveIdLength: 2 bytes, unsigned 16-bit integer
- chainIdLength: 2 bytes, unsigned 16-bit integer
- parentArchiveIdLength: 2 bytes, unsigned 16-bit integer
- sourceIdLength: 2 bytes, unsigned 16-bit integer
- createdUtcEpochMs: 8 bytes, signed 64-bit integer
- kdfIdentifierLength: 2 bytes, unsigned 16-bit integer
- kdfParametersLength: 2 bytes, unsigned 16-bit integer
- saltLength: 2 bytes, unsigned 16-bit integer
- encryptionAlgorithmIdentifierLength: 2 bytes, unsigned 16-bit integer
- nonceLength: 2 bytes, unsigned 16-bit integer
- archiveId: variable bytes, UTF-8 string
- chainId: variable bytes, UTF-8 string
- parentArchiveId: variable bytes, UTF-8 string, may be empty if none
- sourceId: variable bytes, UTF-8 string
- kdfIdentifier: variable bytes, ASCII string, value "argon2id"
- kdfParameters: variable bytes, canonical binary representation of Argon2id parameters
- salt: variable bytes, random salt
- encryptionAlgorithmIdentifier: variable bytes, ASCII string, value "AES-256-GCM"
- nonce: variable bytes, random nonce

This is a recommended fixed-layout header. It is intentionally not defined in the spec, but is a concrete, reviewable design.

### 5.4 Header serialization rules

Required by the specification:
- The public header is serialized in a canonical byte form before encryption.
- The exact serialized bytes are used as AAD.
- They must not be semantically reconstructed before authentication.

Recommended implementation decision:
- Use a canonical binary serialization with little-endian integer encoding and UTF-8 string encoding.
- Do not reorder fields during serialization.
- Do not normalize strings beyond their exact UTF-8 bytes.
- Do not include whitespace or pretty-printing in header serialization.

Open question requiring approval:
- Whether the file should use little-endian or big-endian integer encoding.

> The implementation must pick one and then serialize it canonically. The specification does not define endianness.

---

## 6) Exactly which header bytes are supplied as AES-256-GCM AAD

This is a mandatory requirement.

### 6.1 Required by specification

- The exact serialized public-header bytes are passed to AES-GCM as AAD.
- The header must not be reconstructed or semantically re-serialized before authentication.
- The authentication tag is stored outside the authenticated header and is not included in AAD.

### 6.2 Recommended implementation decision

- The AAD is the exact raw byte array of the serialized public header.
- AAD is created once, immediately after serializing the header and before encryption begins.
- The implementation must not create a second canonical representation of the same header for AAD.
- AES-GCM encrypts the payload using the exact same AAD bytes as used for decryption verification.

### 6.3 Validation rule

A reader must:

1. read the public header bytes
2. deserialize the header
3. compute the same exact AAD from the exact serialized bytes it read
4. use that exact byte array for AES-GCM authentication of the encrypted payload

If the header bytes are modified in storage or transmission, authentication fails.

This is what prevents silent tampering with public metadata.

---

## 7) Where the authentication tag is stored

Required by the specification:
- The authentication tag is stored outside the authenticated header.
- It is not included in the AAD.

Recommended implementation decision:
- Store the GCM tag as the final bytes of the archive file.
- The file layout is `[header][encrypted payload][gcm tag]`.
- The tag length is fixed at 16 bytes for AES-GCM.

This is a clean and easy-to-validate layout.

---

## 8) Encrypted payload structure

The encrypted payload is the content section after the public header and before the final tag.

### 8.1 Required by the specification

- The payload contains the compressed manifest and file data.
- The complete compressed payload is encrypted.
- ZIP-specific encryption must not be used.
- The manifest is inside the encrypted payload.

### 8.2 Recommended implementation design

Use a single encrypted payload block containing a compact archive object.

The payload object is structured as:

- payloadHeader
- manifest
- filePayloadEntries
- directoryEntries
- endOfPayloadMarker

The payload itself is compressed before encryption.

This gives a clear separation between:

- metadata (manifest) for archive identity and file inventory
- file content data for actual file restoration
- optional deletion records for incremental archives

### 8.3 Payload object layout

Recommended payload object:

1. `payloadFormatVersion` (2 bytes)
2. `manifestLength` (4 bytes)
3. `manifest` (UTF-8 JSON bytes)
4. `fileEntryCount` (4 bytes)
5. `fileEntryRecords` (sequence of records)
6. `directoryEntryCount` (4 bytes)
7. `directoryEntryRecords` (sequence of records)
8. optional `deletedEntryCount` and `deletedEntryRecords` if needed

Each `fileEntryRecord` contains:

- relativePath
- operation
- fileSize
- lastModifiedUtc
- sha256
- contentRef

A `contentRef` is an internal payload reference, not a ZIP-specific identifier.

This is essential: the format should describe file content references generically, not as “ZIP entries” or “ZIP file names.”

### 8.4 Relationship to compression

Required by the specification:
- The manifest and file contents are compressed before encryption.
- The payload is not compressed after encryption.

Recommended implementation decision:
- The payload object is compressed as one stream using ZIP or another lossless compressor prior to AES-GCM encryption.
- The public header and AAD remain uncompressed, as required.
- The compressor is internal to the archive writer and not visible as a part of EVA’s external format.

This preserves the required separation between format and compressor.

---

## 9) Manifest schema

The manifest is JSON and is encrypted within the payload.

### 9.1 Required by the specification

The manifest should include at least:
- formatVersion
- archiveId
- created
- archiveType
- chainId
- parentArchiveId
- sourceId
- file information
- directory information

### 9.2 Recommended manifest schema

Recommended Version 1 manifest:

{
  "formatVersion": 1,
  "archiveId": "2026_08_29_1430_01",
  "createdUtc": "2026-08-29T14:30:00Z",
  "archiveType": "snapshot",
  "chainId": "2026_08_29_1200_01",
  "parentArchiveId": null,
  "sourceId": "d2d9...",
  "files": [
    {
      "relativePath": "Characters/Alice.md",
      "operation": "added",
      "fileSize": 12345,
      "lastModifiedUtc": "2026-08-29T14:21:00Z",
      "sha256": "...",
      "contentRef": {
        "kind": "payload-object",
        "objectId": "file-0001",
        "length": 12345,
        "compression": "none"
      }
    }
  ],
  "directories": [
    {
      "relativePath": "Characters",
      "operation": "added",
      "lastModifiedUtc": "2026-08-29T14:21:00Z",
      "exists": true
    }
  ],
  "deleted": [
    {
      "relativePath": "Old/Unused.md",
      "operation": "deleted",
      "deletedAtUtc": "2026-08-29T14:30:00Z"
    }
  ]
}

### 9.3 File information

For each file represented by the archive, the manifest records:

- `relativePath`: path relative to source root
- `operation`: one of `added`, `modified`, `deleted`, `moved` (move is optional optimization; Version 1 may encode as delete + add)
- `fileSize`: size in bytes
- `lastModifiedUtc`: file modification timestamp
- `sha256`: SHA-256 hash of file contents
- `contentRef`: internal payload reference to the actual file content

Required by specification:
- relative path
- operation, for incremental archives
- file size
- last modified timestamp
- cryptographic hash
- archive location/content reference

Recommended implementation decision:
- use one canonical JSON object for each file entry
- use `null` or omitted property for parent archive ID when not applicable
- use UTC timestamps for all manifest timestamps

Open question requiring approval:
- Whether move detection is explicitly recorded as a first-class operation or always represented as delete + add.

### 9.4 Directory information

Required by specification:
- The manifest must record directories so that exact restore can reconstruct directory structure, including empty directories.

Recommended implementation design:
- Represent directories as explicit JSON entries with `relativePath` and a boolean or operation state.
- Record an empty directory entry even when no files are present in it.

Example:

{
  "relativePath": "History",
  "operation": "added",
  "exists": true
}

This ensures empty directories are reconstructed on restore even if no file entries exist beneath them.

### 9.5 Deleted files

Required by the specification:
- Deleted files are represented by manifest entries indicating deletion.

Recommended implementation design:
- Use an explicit `deleted` list or file entry with `operation: "deleted"`.
- For delete semantics, `contentRef` need not exist and may be omitted.

This is necessary for incremental archives, because a changed tree can include deletions as well as additions and modifications.

### 9.6 Content references without making ZIP part of the EVA format

This is a core design requirement.

Required by the specification:
- The manifest stores a reference identifying the file’s content within the compressed archive payload.
- EVA must not depend on ZIP-specific encryption or ZIP-specific semantics being externally visible.

Recommended implementation decision:
- Define a generic `contentRef` object inside the manifest with the following fields:
  - `kind`: `payload-object`
  - `objectId`: stable identifier string within the payload
  - `length`: byte length of the content object
  - `compression`: optional algorithm identifier, such as `none` or `deflate`

Then the payload contains an object store keyed by `objectId`.

This is not ZIP-specific. It describes a payload object that is part of the EVA archive, not a ZIP entry. The implementation may use ZIP internally, but the format itself exposes a generic object model.

---

## 10) Snapshot and incremental representation

Required by specification:
- A snapshot contains the complete source tree at a point in time.
- An incremental archive contains only changes since the immediate parent archive.
- A new snapshot starts a new chain.
- Each archive has archiveId, chainId, and parentArchiveId.
- The non-empty chain may be represented as repeated chain relationships.

Recommended implementation design:

- `archiveType` in the public header and manifest is a value:
  - `snapshot`
  - `incremental`
- `parentArchiveId` is null for a snapshot.
- For incremental archives, `parentArchiveId` identifies the immediately preceding archive in the chain.
- `chainId` remains constant across the chain.

This means the archive can be validated without any local database because all relationships are self-contained.

---

## 11) Empty directory representation

Required by specification:
- The manifest must record directories so an exact restore can reconstruct directory structure, including empty directories.

Recommended implementation design:
- Create explicit directory records for every directory that exists in the represented restore point, including empty directories.
- A directory record contains at least:
  - `relativePath`
  - `operation`
  - `exists`
  - `lastModifiedUtc` (optional)

Example:

- `relativePath: "Notes"`
- `operation: "added"`
- `exists: true`

This ensures restore can create empty directories even when they contain no files.

---

## 12) Archive format versioning

Required by the specification:
- The EVA format must be versioned.
- The manifest repeats archive identity fields from the public header.

Recommended implementation design:

- Use an integer `formatVersion` in both the public header and manifest.
- For Version 1, set `formatVersion = 1`.
- If the reader encounters an unsupported version, it must reject the archive as incompatible.
- The version is independent of file naming and archive chain membership.

Recommended semantics:
- `formatVersion` in the public header is the authoritative version for the archive file format.
- `formatVersion` in the manifest must match the header.
- If they do not match, the archive is invalid.

This rule is important because the archive is intended to be independently verifiable without any local EVA state.

---

## 13) Malformed, truncated, corrupted and tampered archive detection

Required by specification:
- The app must detect incomplete archive chains.
- It must not treat incomplete or corrupted archives as valid.
- Archive verification failure must prevent final commit.
- AES-GCM authentication must be checked before trusting decrypted data.

Recommended implementation validation flow:

1. Check file exists and is large enough to contain a header and tag.
2. Read public header.
3. Validate header magic and formatVersion.
4. Validate header lengths and field boundaries.
5. Check that `headerLength` and `payloadLength` are consistent with file length.
6. Check that payload length is at least non-zero and that the final tag exists.
7. Extract the exact raw header bytes and use them as AAD.
8. Attempt AES-GCM decryption/authentication of the payload using the derived key.
9. Reject the archive if the tag fails or the payload is truncated.
10. Decompress the authenticated plaintext payload.
11. Parse the JSON manifest.
12. Verify that manifest identity fields match the public header.
13. Verify directory and file records are structurally valid.
14. Reject if any required metadata is missing or contradictory.

The archive is considered invalid if any of the following occur:

- magic mismatch
- unsupported format version
- truncated header
- invalid field lengths
- payload shorter than expected
- missing final tag
- GCM authentication failure
- manifest/header field mismatch
- invalid JSON
- invalid file or directory record structure

This is the critical basis for safe failure behavior.

---

## 14) Independent validation without local state

Required by the specification:
- Every `.eva` file must be independently cryptographically verifiable, authenticated, decrypted, decompressed and parsed without requiring external EVA state.
- An incremental archive may still depend on its parent chain for reconstructing a restore point, but it must still be independently valid.

Recommended implementation rule:
- A reader must be able to validate an archive using only:
  - the archive file itself
  - the user password
  - the public header fields
  - the derived KDF parameters in the header

This means the archive must contain everything needed to:

- identify itself
- establish chain membership
- derive the AES key
- decrypt the payload
- validate the tag
- parse the manifest
- determine whether the archive is logically structurally valid

A local database is optional performance metadata, not a requirement for correctness.

---

## 15) Interaction between AES-GCM and Argon2id in the archive format

Required by the specification:
- Use Argon2id as the password KDF.
- Use AES-256-GCM as the encryption algorithm.
- Generate a random salt per archive.
- Generate a random nonce per archive.
- Store algorithm information and KDF parameters in the archive.
- The encryption key must never be stored inside the archive.

Recommended design:

1. The user supplies a password.
2. The implementation reads the header KDF identifier and parameter block.
3. It applies Argon2id with the archive-specific salt and KDF parameters to derive a 256-bit key.
4. The AES-GCM key is the derived 256-bit key.
5. The AES-GCM nonce is read from the header.
6. The exact serialized public-header bytes are used as AAD.
7. AES-GCM encrypts the compressed payload.
8. The GCM tag is written as the final bytes of the archive.

This design makes the archive self-sufficient and portable across machines while keeping the password and key material outside the archive.

---

## 16) In-memory processing vs temporary files

Required by the specification:
- Temporary files may be used if in-memory processing is undesirable.
- Temporary files must be stored in an appropriate private temporary location.
- They must not be exposed as valid `.eva` archives.
- They must be deleted after successful completion.
- The preferred implementation should stream compression directly into encryption to avoid creating a complete unencrypted intermediate archive on disk.

Recommended implementation decision for Version 1:

- Use streaming compression into encryption for normal operation.
- Do not write a full unencrypted archive to a destination directory as a final archive.
- Use a private temporary file only for an intermediate working artifact if the implementation cannot stream entire payloads in memory.
- The temporary file must not have a final `.eva` extension and must be isolated from the user-visible archive directory.

For the expected Version 1 workload, this is the recommended approach:

- Small to medium file collections under typical desktop backup conditions
- Use in-memory processing for archive creation and reading if the total payload is manageable
- Use temporary files only when the write path or file size exceeds practical memory constraints

This keeps the design simple and safe without violating the archive safety rules.

### If temporary files are used

They must:
- be created in a private temp directory
- not be visible as valid final archives
- be flushed and verified before rename to final `.eva`
- be removed on failure

---

## 17) Required decisions about archive naming and atomic commit

This is a required operational rule but not a format-serialization rule:

- final `.eva` files must not appear until the archive is fully written and verified
- the implementation must use a temporary filename first
- final rename is the commit point

This is not encoded within the archive itself, but it is critical to the safe publication of valid archives.

---

## 18) Ambiguities and contradictions to flag explicitly

The specification contains some important design intent but not every byte-level format choice is fully specified. These are not contradictions, but they are items that must be resolved as implementation decisions instead of silently treated as requirements.

### 18.1 Header endianness

The specification does not define whether fixed-width header integers are little-endian or big-endian. This must be chosen.

### 18.2 Exact internal payload object model

The specification requires a compressed and encrypted archive payload containing manifest and file content, but it does not define the exact binary layout of the payload object store. This is an implementation design decision.

### 18.3 File content reference format

The specification requires a reference identifying file content within the compressed payload, but does not define the exact representation. This is intentionally left abstract and must be concretized in the design.

### 18.4 Whether manifests are canonicalized or normalized

The specification says the manifest is JSON without defining field order or whitespace rules. This must be canonicalized by the implementation to ensure consistent validation.

### 18.5 Whether the compressed payload is one stream or many segments

The specification says the compressed manifest and files are part of the payload, but does not define whether the compressed payload is one continuous stream or many compressed blocks. This is an implementation choice.

### 18.6 Exact handling of move operations

The specification explicitly says move detection is optional and not required in Version 1. It allows representing a move as delete + add. It does not require any specific move encoding. This is a design decision.

---

## 19) Proposed final archive object model

The resulting archive format design is:

- `EVA Header` — public metadata, authenticated as AAD
- `Encrypted Payload` — compressed payload object containing manifest and file content references
- `Authentication Tag` — final GCM tag bytes

This structure meets the specification while staying implementable and portable.

The archive is self-describing, chain-aware, and independently verifiable.

---

## 20) Final decision classification

This section summarizes each decision by category.

### Required by the functional specification

- Archive file is `.eva` with public header + encrypted payload + tag.
- Header is unencrypted and small.
- Header contains archive management metadata only.
- Exact serialized header bytes are used as AES-GCM AAD.
- Authentication tag is stored outside the header and not in the AAD.
- AES-256-GCM is mandatory.
- Argon2id is mandatory.
- Random salt and nonce are per archive.
- Manifest is stored inside the encrypted payload and is JSON.
- Archive chain metadata must include archiveId, chainId, and parentArchiveId.
- Snapshot and incremental archives are distinct types.
- Compression occurs before encryption; ZIP encryption is forbidden.
- Temporary files may be used only for transient work and must not be exposed as valid archives.
- Archive creation must be atomic.
- Incomplete or tampered archives must be rejected.
- Archive validation must not require local database state.

### Recommended implementation decision

- Use little-endian binary fields for fixed-width header values.
- Use UTF-8 for manifest and string serialization.
- Use a single compressed payload object containing manifest and file-object records.
- Use a generic `contentRef` object rather than exposing ZIP as a format concept.
- Use UTC timestamps in manifest and header.
- Use a canonical JSON manifest schema.
- Use a private temp-folder location for transient work.
- Prefer streaming compression into encryption over a large disk-based unencrypted artifact.
- Use a local metadata index for performance only, not as a correctness source.

### Open question requiring approval

- Endianness (little-endian vs big-endian) for fixed-width fields.
  Resolved: little-endian. This aligns with .NET BinaryWriter and BinaryReader defaults and requires no additional implementation work.
- Exact binary representation of `kdfParameters` and `contentRef` objects.
  Exact binary representation of kdfParameters
    Resolved: a fixed-width little-endian binary struct of exactly 16 bytes, fields in this order:
      Memory cost in kibibytes (4 bytes, uint32)
      Time cost / iterations (4 bytes, uint32)
      Parallelism / lanes (4 bytes, uint32)
      Output length in bytes (4 bytes, uint32)
    V1 default values: memory = 65536 (64 MB), iterations = 3, parallelism = 4, outputLength = 32.
  Exact binary representation of contentRef
    Resolved: no binary form required. The contentRef exists only inside the encrypted manifest, which is JSON. The JSON object form already defined in Section 9 is the authoritative representation.
- Whether to model move operations explicitly or encode them as delete + add.
  Resolved: delete + add for V1. No first-class move encoding. The spec already permits this and move detection adds unnecessary complexity for a writing backup context where moved files are rare and restore semantics are identical either way.
- Whether to allow a degraded archive-inspection mode for incomplete chains.
  Resolved: no degraded mode in V1. An incomplete chain must be reported clearly to the user and restore must be blocked. A partially restorable chain must never be presented as valid.
- Whether to preserve timestamp precision and timezone semantics exactly or normalize to UTC with explicit precision rules.
  Resolved: UTC throughout, millisecond precision. Specifically:
  - Binary header field createdUtcEpochMs: signed int64, milliseconds since Unix epoch
  - All manifest timestamp strings: ISO 8601 UTC format, e.g. 2026-08-29T14:30:00.000Z
  - Restored file last-modified timestamps: converted from stored UTC to local time at write time, as required by the Windows filesystem API
  Millisecond precision is specified explicitly to avoid spurious change detection caused by timestamp rounding on restore-then-backup cycles.

---

## 21) Final recommendation before implementation

The design above is sufficiently detailed to implement `ArchiveWriter` and `ArchiveReader` without requiring further architectural decisions about the archive contract itself.

The only remaining items that should be explicitly approved are the format-level implementation choices above, not the overall specification or security model.

This gives a reviewable and stable contract for implementation while preserving the specification without altering it.
