# Feature Specification: macOS Backup and Recovery

**Feature Branch**: `feature/macos-backup-recovery`

**Created**: 2026-10-12

**Status**: Draft

**Input**: User description: Add macOS backup and recovery support to EVA while preserving
Windows behavior and existing .eva archive compatibility. Inspect governance, specifications,
architecture, implementation and tests before planning or production changes. Retain the existing
.NET/Avalonia shared pipeline and App/Core/Infrastructure/Tests structure. Cover filesystem
identity, portable paths, special files, complete scans, captured-byte integrity, Keychain,
permissions and unavailable storage, recovery metadata, safe publication, secondary copies, and
the existing 512 MiB limit. Add Windows regressions and macOS integration coverage and report
actual platform evidence. Startup, signing and distribution are separately scoped.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Back up Mac working files safely (Priority: P1)

A Mac user configures a source and ordinary archive directories, saves their password securely,
and creates a snapshot followed by incremental backups without losing source information.

**Why this priority**: A successful backup must protect the actual source contents and names.

**Independent Test**: Back up a Mac fixture containing regular, hidden, Unicode and empty entries;
inspect its recorded names and contents, then modify and delete entries and repeat the backup.

**Acceptance Scenarios**:

1. **Given** a readable source on a case-sensitive volume containing `Note.txt` and `note.txt`,
   **When** snapshot and incremental backups run, **Then** both identities, their contents and
   changes remain distinct in archives, persisted state and content listings.
2. **Given** hidden files, package directories, Unicode names and names valid on macOS but invalid
   on Windows, **When** a backup runs, **Then** all supported entries retain their spelling and
   contents without renaming or treating literal filename characters as separators.
3. **Given** an unreadable subtree, an unavailable source volume, an unavailable cloud-managed
   file, or an unsupported source entry, **When** a backup runs, **Then** it fails explicitly,
   identifies the affected path and remedy, publishes no partial backup and advances no successful
   scan state or successful-backup timestamp; missing entries do not become deletions.
4. **Given** a file changes after scanning or during packaging, **When** a backup runs, **Then**
   published bytes match their recorded size and hash or the run fails without publication.
5. **Given** a complete scan of a previously backed-up source with a real deletion or case-only
   rename, **When** an incremental backup runs, **Then** replay reconstructs the intended new state.

---

### User Story 2 - Recover a trustworthy restore point (Priority: P1)

A user restores a Mac or Windows restore point to a separate destination and receives an explicit
failure before destination writes when its names cannot be represented safely.

**Why this priority**: Backups only provide protection when recovery preserves the promised data.

**Independent Test**: Restore existing Windows fixtures and Mac fixtures to compatible and
incompatible destination volumes; compare contents, paths, empty directories and timestamps.

**Acceptance Scenarios**:

1. **Given** an intact snapshot/incremental chain, **When** restored to a compatible volume,
   **Then** recorded file bytes, lengths, relative paths, empty directories and supported UTC
   modification timestamps match the selected restore point.
2. **Given** names that collide under destination case or Unicode equivalence rules, or a name
   invalid on the destination, **When** restore is requested, **Then** preflight reports every
   detected conflict and performs no destination creation, overwrite or deletion.
3. **Given** a malicious traversal path, a symlink in the destination path, or a source alias,
   **When** restore is requested, **Then** no write escapes the chosen root or modifies the source.
4. **Given** an existing supported Windows .eva chain and only its password, **When** opened on
   macOS without local indexes, **Then** listing, authentication and recovery remain available.
5. **Given** a malformed, tampered or incomplete chain, **When** recovery is requested,
   **Then** it fails explicitly rather than reconstructing a misleading partial restore point.

---

### User Story 3 - Use platform secure password storage (Priority: P1)

A Mac user saves, retrieves, changes and removes EVA's backup password through the application's
normal settings flow and native secure storage.

**Why this priority**: Unattended backups must not require insecure password persistence.

**Independent Test**: Exercise the normal settings lifecycle with an isolated secure-storage item
on macOS, and verify Windows still uses its existing secure storage behavior.

**Acceptance Scenarios**:

1. **Given** a saved password, **When** the app restarts, **Then** macOS retrieves it from Keychain
   and Windows retains Credential Manager behavior; neither platform needs plaintext settings.
2. **Given** denied or unavailable secure-storage access, **When** saving or retrieving a password,
   **Then** the user receives an actionable failure and no insecure fallback is created.
3. **Given** a password change, **When** the next backup runs, **Then** a new full snapshot starts
   the new-password chain and earlier archives remain recoverable with their original password.
4. **Given** credentials and diagnostics, **When** password operations run, **Then** passwords,
   derived keys and file contents appear in neither logs nor shell arguments.

---

### User Story 4 - Trust primary and secondary archive status (Priority: P2)

A user writes archives to local or removable storage and can distinguish primary success from
secondary-copy failure without risking a previously valid archive.

**Why this priority**: Storage interruption must not destroy earlier recovery points.

**Independent Test**: Interrupt primary publication and secondary copying; validate previous
archives, archive discovery and retries.

**Acceptance Scenarios**:

1. **Given** a valid prior archive and an injected publication failure or filename collision,
   **When** publishing another archive, **Then** the prior archive remains byte-for-byte intact
   and unfinished output never appears as a valid final .eva file.
2. **Given** a successful primary archive and an unavailable secondary volume, **When** copying,
   **Then** primary success and pending secondary failure are reported separately and retrying
   eventually publishes a verified complete copy without destroying an existing valid copy.
3. **Given** a full-backup source exceeding 512 MiB, **When** a snapshot is requested, **Then**
   the existing limit remains explicit and enforced, with no new archive or successful-state update.
4. **Given** a long backup or restore, **When** it runs, **Then** the interface continues to accept
   input and displays progress and final primary/secondary outcomes.

### Edge Cases

- Case-only rename; distinct names differing by case; Unicode composed/decomposed forms; migration
  of scan state created with case-insensitive identity rules.
- Literal backslash, colon, trailing space/dot and Windows device names; rooted, empty, dot-dot,
  overlength and malformed archive paths; file-versus-directory conflicts.
- Symlinked source root, file or directory; broken links; loops; links outside the source; junctions
  on Windows; destination ancestors replaced with links after preflight.
- Hard-linked regular files; hidden dotfiles; package directory contents; FIFO, socket and device
  entries; source content disappearing during enumeration.
- Denied traversal versus denied file reads; privacy denial versus ordinary filesystem permissions;
  removable source, primary or secondary volume disappearing mid-operation.
- Cloud placeholders that hydrate successfully versus files that cannot be read; no provider API
  access and no claim that unavailable placeholder data was backed up.
- Empty source; newly empty directory; removed directory; metadata-only modification; files changing
  while retaining size/timestamp; cancellation before publication or during a secondary retry.
- Restore timestamp precision unsupported by destination; Keychain locked/denied/missing item;
  archive name collision; primary and secondary on different volumes; exactly-at-limit source size.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: EVA MUST support macOS backup and recovery using the existing shared application
  workflow and preserve existing supported Windows operations, scheduling and archive chains.
- **FR-002**: Path identity MUST be correct across scanning, persisted state, manifests, reconstructed
  listings, previews, extraction, restore, retry queues and configuration on both case-sensitive
  and case-insensitive source/destination volumes; OS name alone MUST NOT determine volume behavior.
- **FR-003**: EVA MUST preserve source-exposed filename spelling and Unicode without silently merging,
  normalizing, renaming or dropping distinct entries, including case-only changes and deletions.
- **FR-004**: Archive path interpretation MUST be distinct from host filename validation. Valid Mac
  filename characters MUST survive backup, listing and compatible restore without ambiguity or
  accidental conversion into path separators.
- **FR-005**: Restore MUST preflight the complete selected restore point against destination naming,
  case and Unicode equivalence rules and existing entries. Unrepresentable names, duplicate target
  identities and file/directory conflicts MUST fail before destination mutations, with affected
  paths identified; no automatic rename or partial skip is permitted.
- **FR-006**: All extraction and restore paths MUST reject traversal and prevent writes through
  symlinks, junctions or other aliases outside the chosen root, including changes after preflight.
  Configuration MUST reject recursive source/destination relationships and restore over the source,
  including equivalent paths and filesystem aliases.
- **FR-007**: The initial policy MUST reject encountered symlinks, junctions and nonregular files
  with explicit unsupported-entry failures, without following their targets or entering loops.
  This applies to configured source roots and entries below them. Unsupported entries MUST NOT
  disappear silently from a successful backup.
- **FR-008**: Hard-linked regular files MUST be backed up as independent file paths and restored as
  independent regular files; hard-link identity is excluded. Package directories MUST be traversed
  as ordinary directories and hidden regular files/directories MUST be included.
- **FR-009**: Scanning MUST retain metadata-first change detection, periodic full hash verification,
  and hash-based stability checks. Unreadable, unstable, incomplete or unsupported source scans
  MUST fail the run without inferred deletions, published partial archives or successful-state
  advancement. Confirmed deletions require a complete successful scan.
- **FR-010**: Every published content record MUST have size and hash matching the bytes actually
  packaged. A detected scan-to-package mismatch MUST fail or complete the existing bounded stability
  retry policy before publication; source files MUST remain unchanged.
- **FR-011**: macOS MUST use Keychain for persisted passwords and Windows MUST retain its existing
  secure password storage. Platform selection MUST occur at application composition behind
  `IPasswordStore`; UI code MUST NOT construct the concrete Windows store. Password-dependent
  automated tests MUST use isolated fakes except explicitly isolated native integration checks.
- **FR-012**: Password failures MUST be actionable and MUST NOT cause plaintext fallback, shell
  argument exposure, secret logging, machine-bound archives or bypassed password-change snapshots.
- **FR-013**: Permission/privacy failures, unavailable volumes and unavailable cloud-managed content
  MUST produce path-specific actionable errors and distinguish primary failure from secondary
  failure. EVA MUST NOT create an ordinary replacement directory at a missing mounted-volume path
  and report that as successful storage access.
- **FR-014**: Recovery MUST preserve file bytes, lengths, relative paths, empty directories and
  recorded file/directory UTC modification timestamps at the existing millisecond precision where
  supported. Unsupported timestamp precision MUST be reported explicitly rather than claimed exact.
  Directory timestamps MUST describe final restored structure, not intermediate write operations.
- **FR-015**: The recovery contract MUST explicitly exclude POSIX permissions, executable bits,
  ACLs, extended attributes, resource forks, hard-link identity, creation timestamps and original
  file attributes. Users MUST see these limitations before relying on exact Mac recovery; EVA MUST
  NOT claim recovery of complete application installations, bootable systems or excluded metadata.
- **FR-016**: Primary publication and secondary initial/retry copies MUST verify complete temporary
  output before final publication, preserve existing valid archives on failure/collision and keep
  partial files out of archive discovery. Primary failure MUST fail the run; secondary failure MUST
  retain the valid primary, report pending status and remain retryable.
- **FR-017**: Existing supported Windows .eva archives MUST remain readable, authenticatable,
  listable and recoverable with password alone on both platforms. The existing archive format and
  cryptography MUST NOT change silently or require old archives to be deleted or rewritten.
- **FR-018**: The plan MUST assess archive path ambiguity and any older payload variants against
  actual readers, writers and independent fixtures. Any incompatibility requiring a format change
  MUST be reported as a blocking design decision before production changes, with migration and
  reader-compatibility consequences; it MUST NOT be hidden under the same format version.
- **FR-019**: The existing 512 MiB full-backup source limit MUST remain unchanged and user-visible.
  The plan MUST assess its reason and memory implications; increasing/removing it or a streaming
  format redesign requires separately approved scope.
- **FR-020**: The application MUST retain .NET/Avalonia and App/Core/Infrastructure/Tests, with
  platform differences isolated within existing boundaries. Separate Mac backup engines, Rust
  rewrites and direct cloud-provider integrations are outside this feature.
- **FR-021**: Before implementation, planning MUST reconcile Windows-only baseline scope, secure
  storage decisions and the expanded recovery exclusions with constitution and architecture
  governance. This specification identifies required amendments without itself changing them.
- **FR-022**: Verification MUST include meaningful Windows regressions and macOS integration tests
  for both case modes, Unicode, Mac-valid names, links, denied permissions, changing files,
  deletions, timestamps, destination collisions, secure storage, publication/copy failures and
  existing Windows archive fixtures. Reports MUST state actual OS/version, filesystem/case mode,
  pass/fail/skip results and untested limitations; mocks MUST NOT count as native platform evidence.

### Key Entities *(include if feature involves data)*

- **Filesystem identity**: A path's original spelling plus the containing volume's actual identity
  and equivalence rules, distinct from its archive representation.
- **Complete source scan**: Recorded supported entries, hashes, timestamps and scan outcome; only a
  complete successful scan can establish deletions and eligible backup state.
- **Restore point**: A validated snapshot and chronological incremental chain with reconstructed
  files/directories and an explicit metadata recovery contract.
- **Restore preflight**: A proposed mapping to destination paths plus representation, collision,
  containment and metadata-capability findings, completed before writes.
- **Secure password reference**: Configuration's opaque reference to platform secure storage;
  portable archives remain recoverable using the password independently.
- **Archive publication outcome**: Verified primary identity and independent secondary completed,
  pending or failed status, including retry eligibility.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every supported file and empty directory in the Mac acceptance fixture recovers with
  matching relative identity and contents; 100% of recorded supported modification timestamps
  match at millisecond precision on tested compatible volumes.
- **SC-002**: All case, Unicode and incompatible-name collision fixtures fail before destination
  mutation when the selected destination cannot represent them; none are silently renamed or lost.
- **SC-003**: Every injected incomplete-scan or changing-content failure leaves source bytes,
  previous valid archives and successful backup state unchanged, with zero false-success results.
- **SC-004**: 100% of the independently retained supported Windows archive fixtures authenticate,
  list and restore on macOS and Windows without archive conversion or local backup state.
- **SC-005**: Every tested primary/secondary interruption leaves either the previous valid archive
  or a newly verified complete archive available; zero partial outputs are discoverable as valid
  restore points, and all secondary failures are separately visible and retryable.
- **SC-006**: All existing applicable Windows regression tests and new macOS acceptance tests pass
  on their actual target platforms; every unexecuted or skipped platform scenario is identified in
  the final evidence report rather than described as verified.
- **SC-007**: Every tested password lifecycle succeeds through its platform's secure storage or
  provides an actionable failure; zero test-observed passwords appear in settings, logs or process
  arguments. The existing size-limit boundary continues to fail safely above 512 MiB.

## Assumptions

- This feature covers ordinary working-file backups, not a Time Machine replacement or complete
  Mac filesystem image. Excluded metadata is disclosed rather than silently promised.
- Safe initial policy rejects links and nonregular entries for the whole run; supporting link
  restoration is future scope. This deliberate default avoids traversal and format changes.
- Hidden files and packages are included; hard-link relationships are not preserved. Permissions
  on restored files follow the destination's normal creation policy; executable recovery is not
  promised.
- Source filename spelling means spelling exposed by the filesystem; EVA cannot recover Unicode
  distinctions the source filesystem itself does not retain.
- Destination errors discoverable during preflight prohibit all destination mutations. This does
  not promise transactional rollback of an entire restore after an unpredictable runtime I/O error;
  such an error must fail honestly and identify any incomplete restore.
- Existing archive compatibility means the format(s) the current released/supported reader can
  interpret. The writer contains a warning about a superseded JSON payload; planning must establish
  whether historic JSON archives are supported and surface any gap before implementation.
- Literal backslash support is required. Whether the current version-1 path representation can
  preserve it without reader ambiguity is a planning compatibility gate, not permission to invent
  an unversioned escape convention.
- The 512 MiB limit remains; startup registration, installer packaging, signing, notarization,
  distribution, CLI/service redesign and unrelated UI redesign are excluded.
- Native test environments for Windows and macOS, including case-sensitive and case-insensitive
  test volumes, are dependencies. Platform availability must be reported honestly.

### Governance and Existing Implementation Findings

The constitution's safety, portable-archive and small-architecture principles support this work.
Its Exact Recovery principle currently enumerates Windows-specific exclusions only, and its test
isolation language names Windows Credential Manager only. Propose an explicit amendment expanding
Mac metadata exclusions and native secret-store test isolation before implementing those contracts;
retain source safety, encryption, chain validation and exact supported timestamps unchanged.

The functional baseline's Purpose and Platform sections define Windows-only scope; password
storage and recovery sections require reconciliation with macOS. Architecture Direction's Scope,
App responsibilities and decisions 6/7 likewise require explicit Windows/macOS and Keychain
reconciliation. The technical design's Windows timestamp API wording and historical payload
assumptions require a compatibility review. No baseline documents are amended by this phase.

Current evidence identifies case-insensitive collections and backslash conversion in scanning,
state, content projection and restoration; Windows filename restrictions in extraction; direct
Windows password-store construction in App/configuration; incomplete-scan paths capable of
success; scan-to-package rereads; missing restored timestamp assignment; delete-before-publish
and temporary output on a separate volume; and unverified overwrite copies to secondary storage.
Existing scanner, archive payload, writer/reader, restore, configuration and failure-visibility
tests provide a regression base but do not establish native Mac filesystem or Keychain behavior.
These are design/verification inputs, not accepted exceptions to the requirements above.
