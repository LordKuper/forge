# Forge contracts v1

Contract version `1.0.0` freezes Stage 0 boundaries.

## Normative files

- `state-machines.json`: exhaustive lifecycle states and permitted transitions.
- `capabilities.json`: required parity across CLI/TUI and Desktop.
- `recommendations.json`: deterministic next-action definitions.
- `configuration.json`: owner scope, defaults, provenance, and write rules.
- `schemas/*.schema.json`: Draft 2020-12 external boundary schemas.
- `../../../tests/contracts/fixtures/contract-cases.json`: representative valid
  and invalid compatibility instances.

Unknown properties are rejected unless a schema explicitly permits them.
Producers write the declared major version. Consumers may accept an equal major
and newer minor only when unknown optional fields can be ignored safely.

The Stage 0 gate builds every schema with the pinned JsonSchema.Net validator,
resolves cross-schema references, requires format validation, and evaluates every
compatibility fixture.

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
| 8 | authorization | `permission_denied` | Policy denied the command |
| 9 | confirmation | `confirmation_required` | Required human confirmation absent |
| 10 | concurrency | `suggestion_stale` | Expected state version no longer matches |
| 11 | workflow | `workflow_blocked` | Durable workflow cannot safely advance |
| 12 | dependency | `dependency_unavailable` | Required immutable input is unavailable |
| 13 | internal | `internal_error` | Sanitized unexpected failure |

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

Breaking field, meaning, state, or transition changes require a new major
directory. Additive optional changes require a minor contract version and
fixtures proving old-reader behavior. Persisted data migrations are explicit,
independent by scope, atomic, and reversible.
