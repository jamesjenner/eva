<!--
Sync Impact Report
Version change: unversioned scaffold → 1.0.0
Modified principles: placeholder principles replaced with:
- I. Source Safety and Honest Failure
- II. Authenticated Encryption and Secret Protection
- III. Exact Recovery and Chain Integrity
- IV. Portable Archives and Filesystem Destinations
- V. Small Architecture and Verifiable Boundaries
Added sections: Product and Architecture Constraints; Development Workflow and Quality Gates
Removed sections: none (template placeholders populated)
Follow-up TODOs: TODO(RATIFICATION_DATE): confirm the original adoption date.
Review note: remove this temporary report before committing the amended constitution.
-->
# EVA Constitution

## Core Principles

### I. Source Safety and Honest Failure

Backup operations MUST leave source files unchanged. Version 1 MUST reject restoration over
the configured source directory. Configuration validation MUST reject recursive relationships
between source and archive destinations. Archive creation MUST use temporary output, verify it,
and publish the final archive atomically only after verification succeeds. Unreadable or unstable
source files, failed encryption, and failed verification MUST produce explicit failure results;
partial work MUST NOT be reported as a successful backup or restore. Primary destination failure
MUST fail the backup; secondary destination failure MUST be reported separately and retried.
These rules protect the working copy and prevent false confidence in unusable backups.

### II. Authenticated Encryption and Secret Protection

Archives MUST use AES-256-GCM with Argon2id password-based key derivation and cryptographically
secure, unique per-archive salts and nonces. The complete compressed payload, including its JSON
manifest, MUST be encrypted. ZIP password encryption MUST NOT be used. The exact serialized
public header bytes MUST be authenticated as additional authenticated data; the authentication
tag MUST remain outside that header. Header fields MUST be treated as untrusted until
authentication succeeds and MUST contain only necessary archive-management metadata, never
source paths, file names, or contents. Passwords MUST NOT be stored in plaintext configuration;
logs MUST NOT contain passwords, encryption keys, or sensitive file contents. Password storage
MUST remain behind the Core abstraction and MUST NOT make archives machine-dependent.

### III. Exact Recovery and Chain Integrity

A restore point MUST reconstruct its recorded directory structure, including empty directories,
relative paths, file contents, sizes, and last-modified timestamps in UTC. Version 1 exactness
excludes Windows ACLs, file attributes, creation timestamps, and NTFS alternate data streams.
Restore MUST validate the required chain and apply its operations in chronological order;
missing or invalid dependencies MUST block restore. Readers MUST reject malformed, truncated,
tampered, inconsistent, and unsupported archives rather than guess their interpretation.
Retention MUST preserve every archive required by a retained restore point. New snapshots MUST
start new chains, and password changes MUST require a complete snapshot before incremental
archives use the new password. Content listings MUST represent the reconstructed restore point
without requiring file extraction to disk. These invariants make historical recovery dependable.

### IV. Portable Archives and Filesystem Destinations

EVA MUST write encrypted, versioned, self-describing `.eva` archives to ordinary filesystem
directories. EVA MUST NOT integrate directly with cloud-provider APIs; external synchronization
services may transport the encrypted files. Archive identity and chain relationships MUST be
recoverable from archive metadata without an external database, a particular machine, or a
particular EVA installation. Local indexes and retry state MAY improve operation but MUST NOT
be the sole source of archive truth. Archive format changes MUST document compatibility and
recovery consequences; unsupported versions MUST produce an explicit error.

### V. Small Architecture and Verifiable Boundaries

EVA MUST retain clear responsibilities across App, Core, Infrastructure, and Tests. Core MUST
NOT depend on App or Infrastructure; Infrastructure MUST implement Core contracts; App MUST
compose the application and own UI concerns. Cryptography, archive validation, storage, logging,
and password handling MUST remain explicit, independently testable boundaries. New projects or
abstractions MUST have a documented product or testing need rather than exist solely to split
responsibilities. Tests requiring password storage MUST use an isolated fake rather than real
Windows Credential Manager access. This structure supports maintenance by a small team while
keeping safety rules independent of the interface.

## Product and Architecture Constraints

EVA Version 1 is a desktop backup and recovery application with a system-tray workflow.
The established UI direction is Avalonia with SukiUI. Background backup and restore operations
MUST keep the interface responsive and expose progress, completion, and failure status.

The functional baseline is `EVA Encrypted Archive & Recovery System.md`. Architecture decisions
are recorded in `Architecture Direction for EVA.md`; archive and cryptographic details are
recorded in `EVA Version 1 Technical Design - Archive Format and Crypto Boundary.md`. Changes
MUST identify conflicts between these documents and resolve them explicitly before implementing
conflicting behavior. This constitution governs enduring safety and development rules; feature
specifications and technical designs provide detailed algorithms, formats, and schedules.

## Development Workflow and Quality Gates

Feature specifications and implementation plans MUST identify applicable constitutional rules
and record how the design satisfies them. Changes to backup, encryption, archive parsing,
restore, retention, configuration safety, or destination handling MUST include automated tests
for affected correctness and failure behavior. Integration tests MUST exercise affected archive
round trips, authentication failures, chain dependencies, and filesystem failure boundaries as
applicable. Fixes for safety defects MUST include a regression test reproducing the defect.

Before merging, reviewers MUST verify source preservation, secret handling, archive compatibility,
restore correctness, and retention dependencies wherever the change affects those concerns.
Relevant builds and tests MUST pass; verification results and any untested platform behavior
MUST be recorded in the change description. Existing implementation gaps MUST be reported
explicitly rather than treated as exceptions to these rules.

## Governance

This constitution governs EVA development. Amendments MUST record the proposed rule changes,
their rationale, compatibility impact, and any migration or remediation work. A project
maintainer MUST approve amendments before they are merged. Conflicting specifications or
implementation decisions MUST be reconciled with the constitution or accompanied by an approved
constitutional amendment; exceptions MUST NOT be implicit.

Constitution versions follow semantic versioning: MAJOR for incompatible principle removals or
redefinitions, MINOR for new principles or materially expanded guidance, and PATCH for wording
clarifications without changed obligations. Each amendment MUST update the version and last
amended date while preserving the original ratification date. Every feature plan and change
review MUST assess compliance; discovered violations MUST be corrected or tracked explicitly
with a remediation plan. Temporary Sync Impact Reports MUST be removed before committing.

**Version**: 1.0.0 | **Ratified**: TODO(RATIFICATION_DATE): confirm original adoption date | **Last Amended**: 2026-10-10
