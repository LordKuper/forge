# Forge contracts v1

The v1 contract family defines the current pre-1.0 boundaries. Until Forge
`1.0.0`, these public contracts are unstable and a Forge MINOR release may remove
or replace them without aliases or a deprecation period. `capabilities.json` and
`state-machines.json` declare their current versions.

## Normative files

- `state-machines.json`: exhaustive lifecycle states and permitted transitions.
- `capabilities.json`: required parity across CLI/TUI and Desktop.
- `recommendations.json`: deterministic next-action definitions.
- `configuration.json`: owner scope, defaults, provenance, and write rules.
- `schemas/*.schema.json`: Draft 2020-12 external boundary schemas.
- `../../../tests/Forge.Tests/Contracts/fixtures/contract-cases.json`: representative valid
  and invalid compatibility instances.

Unknown properties are rejected unless a schema explicitly permits them.
Producers write the declared major version. Consumers may accept an equal major
and newer minor only when unknown optional fields can be ignored safely.

The local Host transport is defined by ADR 0005. It serializes these application
contracts but is not a second capability model. `project.snapshot` is the
authoritative read model and `control.events` is its incremental invalidation
stream; pipe handshakes, framing, leases, and client reconnect are shared
transport/runtime requirements.

The Stage 0 gate builds every schema with the pinned JsonSchema.Net validator,
resolves cross-schema references, requires format validation, and evaluates every
compatibility fixture. The repository `global.json` pins the .NET SDK used by the
gate on workstations and CI.

## Diagnostics and exit codes

| Exit | Category | Code | Meaning |
|---:|---|---|---|
| 0 | success | `ok` | Command completed |
| 2 | usage | `invalid_arguments` | Syntax or validation error |
| 3 | configuration | `configuration_scope_violation` | Key belongs to another scope |
| 4 | project | `project_not_initialized` | Confirmed root lacks valid `.forge/` |
| 5 | platform | `platform_not_supported` | No registered platform strategy |
| 6 | update | `self_update_failed` | Forge update/verification/handshake failed |
| 7 | provider | `provider_update_failed` | Provider install/update/recheck failed |
| 7 | provider | `provider_idle_timeout` | Provider attempt exceeded its no-activity deadline |
| 7 | provider | `provider_session_timeout` | Provider attempt exceeded its absolute deadline |
| 7 | provider | `provider_authentication_required` | Enabled provider has no local authentication |
| 7 | provider | `provider_authentication_check_failed` | Provider authentication probe itself failed |
| 8 | authorization | `permission_denied` | Policy denied the command |
| 9 | confirmation | `confirmation_required` | Required human confirmation absent |
| 10 | concurrency | `suggestion_stale` | Expected state version no longer matches |
| 10 | concurrency | `project_in_use` | Another Forge Host owns the project writer lease |
| 10 | concurrency | `control_cursor_stale` | Event cursor cannot continue; refresh from the returned anchor |
| 11 | workflow | `workflow_blocked` | Durable workflow cannot safely advance |
| 11 | workflow | `review_iteration_limit` | Review requires a human convergence decision |
| 11 | workflow | `review_repeated_findings` | External review repeated an identical normalized finding set |
| 12 | dependency | `dependency_unavailable` | Required immutable input is unavailable |
| 13 | internal | `internal_error` | Sanitized unexpected failure |
| 14 | compatibility | `host_protocol_incompatible` | Client and Host protocol majors cannot communicate |

Machine stdout contains only the requested schema. Diagnostics use stderr and
carry `code`, `category`, `message_key`, typed `arguments`, `correlation_id`, and
optional safe recovery actions. Provider raw output is never a diagnostic code.

## Redaction

Redaction occurs before logging, persistence, telemetry, diagnostic bundles, or
presentation. Field-name rules cover password, secret, token, authorization,
cookie, credential, private key, and provider session fields. Value rules cover
known credentials, bearer/basic headers, JWT-like values, private-key blocks, and
credential-bearing URIs. Replacement is `[REDACTED:<kind>]`; correlation uses an
HMAC fingerprint with an installation-local key, never a raw hash.

Full environment dumps, command-line secrets, prompt-based secret processing,
credential storage in `.forge/`, and publishing unredacted provider output are
forbidden. If safe structured parsing fails, the payload is dropped and a
`redaction_payload_dropped` event is emitted.

## Contract evolution

Before Forge `1.0.0`, field, meaning, state, transition, and API replacements may
remain in this directory with a contract-version increase and current fixtures.
At or after `1.0.0`, breaking changes require a new major directory; additive
optional changes require a minor contract version and old-reader fixtures.
Persisted data migrations remain explicit, independent by scope, atomic, and
reversible at every Forge version.
