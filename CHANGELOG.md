# Changelog

User-facing Forge changes are listed by release, newest first.

## v0.4.0

### Added

- Added a platform-neutral self-update core that detects and normalizes update
  targets, resolves exactly one strategy before release access, selects newer
  stable GitHub releases with ETags, verifies release assets, and coordinates
  staging, restart handshakes, and rollback.
- Added updater contract, architecture, and regression coverage for unsupported
  platforms, release selection, verification failures, restart context, and
  rollback.

## v0.3.0

### Added

- Added the .NET 10 SLNX solution with layered runtime, updater, provider,
  presentation, configuration, localization, bootstrap, CLI, and MAUI Desktop
  projects.
- Added a shared English/Russian localization catalog and a localized CLI status
  command and Desktop startup page.
- Added scoped user/project configuration registries, provenance resolution,
  independent migrations, scope enforcement, and atomic writes with recovery.
- Added unit, integration, acceptance, architecture, security, and installer
  tests plus Windows x64/Arm64 publish profiles.
- Added CI validation for locked restore, formatting, warnings-as-errors builds,
  tests, and high/critical dependency vulnerabilities.

### Fixed

- Made persisted user and project configuration conform to the published v1
  schemas, reject invalid writes, durably flush atomic replacements, and recover
  validated previous revisions.
- Made the MAUI Desktop restore graph deterministic for Windows x64 and Arm64.
- Prevented Generic Host configuration from consuming Forge CLI options such as
  `--help`, and normalized repository text files to LF across clean checkouts.
- Aligned sprint states with the v1 workflow contract and ensured cancellation
  terminates child process trees.

### Security

- Expanded structured and value-based secret redaction to cover every credential
  category required by the v1 contract, including nested payloads.

## v0.2.1

### Added

- Restored the complete original research and target-system design document as a
  verbatim Russian-language source artifact.

### Changed

- Clarified the relationship between the complete source design, the canonical
  English architecture overview, and the implementation plan.

## v0.2.0

### Added

- Defined the accepted MVP boundaries, trust model, state machines, diagnostics, localization, scoped configuration, and presentation parity contracts.
- Added versioned JSON Schemas and machine-readable capability, recommendation, configuration, and lifecycle registries.
- Added an automated contract gate that validates schema identity, closed state transitions, surface parity, recommendation safety, and configuration ownership.
- Added locked Draft 2020-12 meta-schema, reference-resolution, and valid/invalid compatibility-fixture validation.

### Changed

- Required PowerShell 7.6.3 or newer for release validation.
- Required publication workflows to open ready-for-review PRs unless draft status is explicitly requested.
- Required autonomous automatic-review cycles until every actionable finding is resolved.
- Replaced non-English source documents with concise English architecture and implementation-plan artifacts.

## v0.1.0

### Added

- Published the project development and contribution rules.
- Added automatic release validation, version tagging, and GitHub Release publication.

### Fixed

- Prevented releases from reusing the `main` version or publishing from non-`main` branches.
- Enforced semantic version increments and breaking-change declarations while safely publishing concurrent releases.
