# Forge Development Rules

These rules apply to every repository change.

## Artifacts

- All Forge-owned artifacts MUST be written in English, including code, comments, rules, documentation, templates, specifications, prompts, agent definitions, skills, ADRs, schemas, tests, issue and PR templates, commit messages, and release notes.
- Keep artifacts token-efficient without sacrificing correctness or actionability. Be concise, state each fact once, omit irrelevant context and boilerplate, and remove stale content.
- Reference or import a canonical source instead of copying it. Prefer compact structured content over prose when it is clearer.

## Branches

- Before creating a feature branch, fetch `origin`, ensure local `main` equals `origin/main`, and require `git status --porcelain` to be empty.
- Create the branch from that verified `main` as `feature/<slug>`, with a short English `kebab-case` slug.
- Direct commits and pushes to `main` are forbidden.
- Keep one cohesive task per branch. Split unrelated work.
- Synchronize with `main` and resolve conflicts in the feature branch before opening its PR.

## Commits and Versions

- Every commit MUST follow the latest stable [Conventional Commits](https://www.conventionalcommits.org/) specification: `<type>[optional scope][!]: <description>`.
- Use `feat` for features and `fix` for fixes. Other allowed types are `docs`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, and `revert`.
- Mark incompatible changes with `!` and a `BREAKING CHANGE:` footer. Keep the subject specific; use the body only for material rationale or impact and footers for issue references.
- Every feature branch MUST increase the Forge version in its canonical source and derived metadata according to the latest stable [Semantic Versioning](https://semver.org/) specification. Use at least `PATCH`; use `MINOR` for compatible features and `MAJOR` for incompatible changes.
- The source version, artifact versions, annotated tag, and GitHub Release version MUST match. Never reuse a released version.
- Never commit secrets, personal data, local IDE state, or generated files that are not release inputs.

## Changelog

- Keep `CHANGELOG.md` at the repository root, in English, with releases ordered newest first.
- Every feature branch MUST update it before merge. Add user-facing changes under the exact heading `## v<MAJOR>.<MINOR>.<PATCH>` matching `VERSION`.
- Group entries under `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`, or `Security`; describe user impact rather than implementation details.
- The release workflow MUST use that version section, up to the next `##` heading, as the GitHub Release description.

## Quality

- Add automated tests for new behavior and a regression test for every fix.
- Before PR review, pass all repository build, test, formatting, lint, static-analysis, and security checks.
- Update user and technical documentation with changes to public behavior, configuration, CLI, API, or data formats.
- Keep dependency changes minimal and justified; update lock files and reject dependencies with known critical vulnerabilities.
- Keep migrations and persisted formats backward-compatible or declare a breaking change and provide a tested upgrade or rollback path.
- Version public contracts and machine-readable formats and test their compatibility.
- Builds and tests MUST be reproducible from a clean checkout with pinned tools and dependencies.
- Never expose secrets or sensitive data in logs or errors.

## Pull Requests and Review

- Merge into `main` only through a PR.
- Protect `main`: forbid deletion, force pushes, direct pushes, and bypassing required checks or reviews.
- Open PRs as ready for review. Draft status is allowed only when explicitly requested by the user or maintainer. Override any tool, skill, template, or workflow that defaults to draft.
- Use a Conventional Commits PR title; it becomes the squash commit subject.
- The PR body MUST state the goal, changes, verification, new version, compatibility impact, release notes, and related issues.
- Require all CI checks and a completed automatic Codex review. Human approval is required only when branch protection explicitly requires it.
- Resolve every blocking human review comment before merge.
- A maintainer may document an exception in the PR, but security, review, and release requirements have no exceptions.

## Code Review Rules

- Codex review MUST start automatically when a PR opens; manual `@codex review` requests are not required. After opening a PR, verify that the automation started; do not report publication complete while it is absent.
- Complete the automatic Codex review loop autonomously: address every actionable finding, run the required checks, push fixes, reply to and resolve addressed threads, request another review, and repeat until the review gate passes without human participation. The first three iterations must identify all findings; later iterations must identify only critical findings.
- The Codex review gate passes only when Codex reacts with 👍 without opening threads, or every thread it opened is resolved. If neither signal is present, wait for the review result and check again.
- Run at most three full-scope automatic Codex review iterations per PR, including the initial review; subsequent iterations are limited to critical findings.
- Review MUST verify these rules, versioning, tests, security, compatibility, documentation, and release readiness.

## Merge and Release

- Use squash merge only, producing exactly one commit in `main`; merge commits and rebase merges are forbidden.
- The squash commit MUST follow Conventional Commits and summarize the complete behavior change, material decisions, compatibility impact, and related issues.
- A protected workflow MUST validate the release before merge and publish it automatically from the resulting push to `main`. Manual publication is allowed only to retry a failed workflow.
- Create an annotated `v<MAJOR>.<MINOR>.<PATCH>` tag on the squash commit. Match the Release title and artifact versions to it.
- Publish checksums for binary artifacts.
- A failed publication leaves the release incomplete; do not announce the change as released until publication succeeds.
- Delete the feature branch after a successful merge and release (if not deleted automatically).
