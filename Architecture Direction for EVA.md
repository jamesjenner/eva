# Architecture Direction for EVA

## Scope

This document revises the architecture to fit the real constraints of EVA Version 1.

EVA is a single Windows desktop application, not a reusable framework or a collection of independently distributed libraries. The specification is strict about safety, archive integrity, and recovery correctness, but it does not require a large set of separately published .NET projects.

This design therefore prefers a small, maintainable application structure with clear logical separation through folders, namespaces, interfaces, and internal components.

---

## 1) Architectural constraint: keep the solution small and maintainable

For Version 1, the architecture should be optimized for:

- a single developer maintaining the app
- straightforward debugging and testing
- clear security boundaries without excessive project sprawl
- minimal build complexity
- easy understanding of the backup/restore lifecycle

The implementation should not be fragmented merely because a responsibility is conceptually distinct. A responsibility can remain logically separate without requiring a separate .NET project.

This means the project structure should be small, but the code structure inside each project should still be disciplined and modular.

---

## 2) Revised project structure

Recommended solution structure for EVA Version 1:

- EVA.App
  - Windows UI and system tray host
  - startup and shutdown orchestration
  - composition root for the app
  - user-triggered actions: snapshot now, list content, restore, options

- EVA.Core
  - domain models
  - archive contracts and manifest models
  - backup/restore/retention domain logic
  - service interfaces and abstraction boundaries
  - validation rules and policy models
  - password storage abstraction (IPasswordStore)

- EVA.Infrastructure
  - archive serialization and parsing
  - cryptography implementation
  - compression implementation
  - filesystem access and storage operations
  - configuration persistence
  - archive discovery and indexing
  - logging
  - password storage and retrieval (PasswordStore)
  - concrete implementations of Core abstractions

- EVA.Tests
  - unit tests
  - integration tests for archive validation, crypto, restore logic, retention logic, and backup orchestration

This is the appropriate starting point because it keeps the app simple while still maintaining firm technical boundaries.

---

## 3) Dependency direction

The dependency direction should be:

- EVA.App depends on EVA.Core and EVA.Infrastructure
- EVA.Core contains abstractions and domain logic and does not depend on App or Infrastructure
- EVA.Infrastructure depends on EVA.Core and implements its contracts
- EVA.Tests depends on the production projects as needed

This preserves a clean dependency direction while keeping the solution small.

The key rule is:

- infrastructure can implement the core contracts
- the core should never know about the UI or concrete storage implementations

---

## 4) Why each project exists

### EVA.App

Purpose:
- main entry point for the desktop application
- system-tray shell and windows
- startup and shutdown composition
- orchestration of user-initiated operations
- interaction with the background timer and backup workers

Why it exists:
- The app is a Windows desktop application with UI and operational orchestration.
- It should be the composition root.
- It is the only project that should have direct knowledge of Windows desktop concerns.

Dependency direction:
- depends on Core for logic and on Infrastructure for concrete implementations.

---

### EVA.Core

Purpose:
- define the domain rules and core logic of archive creation, validation, restore, retention, and backup decisions
- define archive metadata contracts, manifest models, configuration models, and backup policy models
- define interfaces such as archive readers, archive writers, crypto services, index services, retention evaluators, and password storage
- define IPasswordStore as the abstraction boundary for password storage so that ConfigValidator and other Core components can depend on the abstraction without knowing about Windows Credential Manager or any concrete storage mechanism

Why it exists:
- This is the conceptual heart of the system.
- It defines rules that must never be bypassed.
- It avoids spreading the core logic across UI and infrastructure code.

Dependency direction:
- no dependency on App or Infrastructure
- infrastructure implements the contracts in this project

---

### EVA.Infrastructure

Purpose:
- implement the actual technical behavior behind the core contracts
- contain the concrete implementations for archive serialization, cryptography, compression, filesystem operations, configuration persistence, indexing, logging, and password storage

Why it exists:
- This project holds the concrete mechanics of the specification.
- It is large enough to include multiple technical responsibilities, but still remains a single application-level infrastructure project rather than a library-per-concern explosion.

Dependency direction:
- depends on Core
- is consumed by App

This project should be internally organized by namespace and folder to preserve separation, for example:

- EVA.Infrastructure.Archives
  - archive format, public header, manifest handling, writer/reader, validation

- EVA.Infrastructure.Cryptography
  - Argon2id, AES-GCM, key derivation, AAD validation

- EVA.Infrastructure.Compression
  - ZIP or other internal compression provider
  - payload packaging

- EVA.Infrastructure.Storage
  - destination file handling, temp files, atomic rename, copy verification, secondary destination retry logic

- EVA.Infrastructure.Configuration
  - settings file persistence and config validation

- EVA.Infrastructure.Indexing
  - local archive discovery/indexing cache
  - chain status and archive catalog logic

- EVA.Infrastructure.Logging
  - structured event logging and diagnostics

- EVA.Infrastructure.Security
  - password storage and retrieval (PasswordStore)
  - Windows Credential Manager integration via P/Invoke
  - DPAPI and AES-GCM legacy file fallback
  - implements IPasswordStore from EVA.Core

These are not separate projects; they are logical boundaries inside one infrastructure assembly.

---

### EVA.Tests

Purpose:
- test the actual behavior of archive creation, crypto, chain validation, restore accuracy, retention, and backup failure handling

Why it exists:
- The specification is safety-critical, so testability is important.
- This is the one project where tests are naturally separated from production code.

Dependency direction:
- depends on the production projects being tested
- uses FakePasswordStore (an in-memory IPasswordStore implementation) for tests that require password storage without real Credential Manager access

---

## 5) Responsibilities merged into the same project and why they remain logically separated

This is the main change from the earlier fragmented architecture.

The following responsibilities are intentionally merged into EVA.Infrastructure:

- cryptography
- compression
- archive serialization
- filesystem/storage operations
- configuration persistence
- archive discovery/indexing
- logging
- password storage and retrieval

These are all implementation details of the application, not independently distributable subsystems.

They remain logically separated in the following way:

### By folder and namespace

See the namespace listing in Section 4 above. Each area of responsibility has its own namespace and internal implementation boundary.

### By interfaces in Core

The important architectural boundary is not project count, but service boundaries.

For example:

- IArchiveWriter
- IArchiveReader
- IArchiveValidator
- IPasswordDerivationService
- IAesGcmService
- ICompressionProvider
- ISourceScanner
- IArchiveIndex
- IConfigStore
- IEventLogger
- IPasswordStore

This allows the Core layer to depend on abstractions instead of concrete implementations while keeping all production code in one deployable application.

### By ownership and dependency rules

Even though these concerns are in the same project, they still obey the same dependency discipline:

- archive packaging logic should not reach directly into the UI
- the crypto implementation should not know about Windows Forms or tray concerns
- logging should never accept or emit secrets
- storage should not be responsible for UI decisions
- configuration should not contain backup logic
- password storage must never store plaintext passwords or encryption keys

This keeps the system logically clean without turning every concern into a separate project.

---

## 6) Proposed architecture in plain terms

The application should be organized like this:

- App layer: what the user sees and triggers
- Core layer: what the system is allowed to do and how correctness is defined
- Infrastructure layer: how the system does it technically

This gives EVA a straightforward flow:

1. The app triggers a backup or restore operation.
2. The orchestration code in Core or App calls domain logic.
3. The infrastructure layer performs the concrete archive, crypto, compression, filesystem, and password storage work.
4. The app reports status and logs operational results.

This is sufficient for Version 1 because the app is a single product with one developer and one release process.

---

## 7) Why this structure is the correct tradeoff

This structure is deliberately simpler than a library-per-concern architecture because:

- EVA is not an SDK or framework
- there is no intended NuGet or independent-library distribution model
- the business risk is in correctness, not in reusable component packaging
- the real technical challenge is security and archive integrity, which is best enforced by clear boundaries inside a single application architecture

At the same time, it does not collapse the important separation required by the functional specification:

- backup logic and restore logic are distinct concerns
- archive format rules are distinct from crypto logic
- cryptography is distinct from storage and UI concerns
- authentication and validation must be explicit and testable
- password storage is an infrastructure concern, not a UI concern — PasswordStore lives in EVA.Infrastructure and is accessed through the IPasswordStore abstraction defined in EVA.Core

The app stays simple to understand while still respecting the system's critical integrity boundaries.

---

## 8) Security and testability should remain explicit

Even with just a few projects, the architecture must preserve important security boundaries:

- the public archive header must be treated as unauthenticated until validated
- the AES-GCM implementation must be isolated behind an interface
- the Argon2id KDF must be explicit and testable
- archive validation should not be hidden inside UI logic
- the backup orchestrator should not directly mix runtime UI state with archive correctness logic
- password storage must be behind the IPasswordStore abstraction so that ConfigValidator and other Core components never depend on concrete Windows credential storage mechanisms
- tests must use FakePasswordStore rather than real Credential Manager access to remain isolated and repeatable

The code can remain in a single infrastructure assembly, but it should still be organized and tested as if it were multiple technical subsystems.

---

## 9) Open decisions still required before implementation

The following are still design decisions regardless of project count:

1. UI framework choice

   Decision: Avalonia with SukiUI theming. WPF was the original choice but Avalonia was selected for its cross-platform capability and modern appearance. SukiUI 7.x provides the theme, controls, and dialog system.

2. Archive payload structure details

   Decision: as specified in the format documentation.

3. File stability policy

   Decision: 2-second wait, 3 retries. Stability is confirmed by comparing SHA-256 hashes across two consecutive checks rather than relying on filesystem timestamps, which have coarse resolution on Windows NTFS. Both TryReadFile and ComputeSha256 use FileShare.ReadWrite so files held open by other processes are correctly detected as unstable rather than unreadable.

4. Snapshot scheduling semantics

   Decision: Weekly snapshots: calendar week, triggered on the last scheduled backup check on Sunday before midnight (Sunday 23:59). Monthly snapshots: calendar month, triggered on the first scheduled check on or after the 1st. If the trigger window is missed within the same period, fire on the next available check. If the entire period is missed, fire immediately on resume. A weekly snapshot falling on the 1st satisfies both the weekly and monthly categories — no duplicate snapshot is created.

5. Secondary destination retry model

   Decision: A small local JSON state file recording per-archive copy status.

6. Local secret storage strategy

  Decision: Use Windows Credential Manager as the sole password store via P/Invoke to CredWrite/CredRead. The storage mechanism is not baked into the archive format — archives remain decryptable on any machine using only the user password.

7. Restore exactness policy

   Decision: relative path, file contents, file size, and last-modified timestamp (UTC). Explicitly exclude: Windows ACLs, file attributes (hidden, read-only), creation timestamps, and NTFS alternate data streams.

8. Incomplete chain behavior in UI

   Decision: no degraded mode in V1. If the chain is incomplete, report it clearly and block restore. Keep the validation logic simple and unambiguous for now.

---

## 10) Recommended final project structure

For Version 1, the recommended architecture is:

- EVA.App
- EVA.Core
- EVA.Infrastructure
- EVA.Tests

This is the simplest architecture that still satisfies the formal functional specification without over-fragmenting the solution.

The project count is reduced intentionally, but the responsibilities remain separated through:

- namespaces
- interfaces
- layering
- internal implementations
- tests
- explicit dependency direction

That gives EVA a clean, maintainable structure without creating a disconnected collection of miniature libraries.

---

## 11) Final conclusion

The earlier architecture was directionally correct but too fragmented for a single Windows desktop application that is not intended to be published as a reusable library ecosystem.

The revised architecture keeps the important technical boundaries, but collapses them into a small, practical application structure:

- App for UI and orchestration
- Core for domain logic, contracts, and abstractions including IPasswordStore
- Infrastructure for concrete technical implementation including PasswordStore, archive handling, cryptography, and all storage concerns
- Tests for safety and correctness, using FakePasswordStore for isolated password-related testing

This is the best fit for EVA Version 1: maintainable, secure, testable, and simple enough for one developer to understand and extend.