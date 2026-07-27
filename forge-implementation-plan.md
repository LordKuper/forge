# Forge — поэтапный план реализации MVP

**Дата:** 27 июля 2026 года
**Статус:** рабочий интерактивный план
**Источник требований:** [архитектурный документ](./ai-agentic-software-development-workflow-ru.md)

---

## Как вести план

- Отмечать задачу выполненной (`- [x]`) только после получения проверяемого результата.
- Для каждой завершённой задачи добавлять ссылку на commit, PR, test run или другой evidence в журнале в конце файла.
- Не отмечать gate этапа, пока не выполнены все обязательные задачи и проверки этого этапа.
- Если решение меняет архитектурный контракт, сначала обновить ADR и архитектурный документ.
- Новую работу, не необходимую для gate текущего этапа, помещать в backlog, а не расширять scope этапа.

## Сводный прогресс

- [x] Этап 0. Зафиксировать контракты, угрозы и критерии MVP
- [ ] Этап 1. Создать каркас .NET solution, CLI/TUI и MAUI hosts
- [ ] Этап 2. Реализовать кросс-платформенное ядро self-updater
- [ ] Этап 3. Реализовать Windows installer и Windows update strategy
- [ ] Этап 4. Реализовать startup pipeline и инициализацию `.forge/`
- [ ] Этап 5. Реализовать toolchain updater и adapters для Claude/Codex
- [ ] Этап 6. Реализовать независимые спринты и durable workflow engine
- [ ] Этап 7. Реализовать Git/worktree isolation, fallback и circuit breaker
- [ ] Этап 8. Реализовать `.forge/` SSoT compiler
- [ ] Этап 9. Реализовать memory, context builder и code intelligence
- [ ] Этап 10. Реализовать `implementation-critical` и полный паритет CLI/TUI ↔ MAUI
- [ ] Этап 11. Добавить наблюдаемость, evals и security hardening
- [ ] Этап 12. Собрать, установить и принять MVP-релиз

---

## Этап 0. Контракты, угрозы и границы MVP

**Цель:** устранить архитектурную неоднозначность до написания runtime-кода.

### Решения и ADR

- [x] P0.1 Зафиксировать MVP scope: Windows installer, Windows runtime и только `implementation-critical`.
- [x] P0.2 Зафиксировать, что updater core кросс-платформенный, но в MVP зарегистрирована только `WindowsUpdateStrategy`.
- [x] P0.3 Зафиксировать `.forge/` как единственный канонический каталог project configuration.
- [x] P0.4 Зафиксировать fail-closed поведение при ошибке self-update или provider update/recheck.
- [x] P0.5 Зафиксировать модель независимого спринта и правила явных межспринтовых зависимостей.
- [x] P0.6 Зафиксировать trust boundary между Forge, GitHub Releases, Codex CLI, Claude Code CLI и project content.
- [x] P0.7 Зафиксировать политику latest stable release, SemVer и запрет downgrade.
- [x] P0.8 Зафиксировать обязательные checksum, signature/provenance, self-test, restart handshake и rollback.

### Контракты

- [x] P0.9 Описать состояния и переходы Forge self-update.
- [x] P0.10 Описать `IPlatformUpdateStrategy` и platform-neutral target model.
- [x] P0.11 Описать состояния provider toolchain: `missing`, `installing`, `updating`, `rechecking`, `ready`, `failed`.
- [x] P0.12 Описать lifecycle спринта, узла и attempt.
- [x] P0.13 Определить JSON Schema для manifest, events, handoff, findings и node results.
- [x] P0.14 Определить exit codes и diagnostic categories, включая `platform_not_supported`, `self_update_failed`, `provider_update_failed` и `project_not_initialized`.
- [x] P0.15 Определить redaction policy и запрещённые способы работы с credentials.
- [x] P0.16 Зафиксировать обязательный функциональный паритет CLI/TUI и .NET MAUI Desktop.
- [x] P0.17 Описать versioned capability matrix и общий surface-neutral command/query/event contract.
- [x] P0.18 Зафиксировать запрет business orchestration и отдельных permission rules в presentation projects.
- [x] P0.19 Описать versioned `ProjectStatusSnapshot` и `SuggestedAction` contracts.
- [x] P0.20 Зафиксировать deterministic ranking rules, active-sprint selection и приоритет состояний, требующих внимания.
- [x] P0.21 Зафиксировать, что suggested action не обходит command validation, permissions, confirmations и idempotency.
- [x] P0.22 Зафиксировать localization scope: default/fallback `en`, встроенный `ru`, общий catalog для CLI/TUI и MAUI.
- [x] P0.23 Описать inheritance и session overrides для `language.ui`, `language.interaction` и `language.llm`, с ultimate fallback `en`.
- [x] P0.24 Зафиксировать culture-neutral commands, flags, schemas, codes, telemetry и persisted state.
- [x] P0.25 Описать language-pack contract: keys, typed placeholders, plural/select rules, manifest, fallback и compatibility.
- [x] P0.26 Зафиксировать непересекающиеся user/project key spaces, owner scope и `configuration_scope_violation`.
- [x] P0.27 Описать user-scope schema для `language.ui`, `language.interaction`, `language.llm` и interaction preferences.
- [x] P0.28 Описать project-scope schema для `artifacts.language.user_facing` и `artifacts.language.agent_facing`.
- [x] P0.29 Зафиксировать artifact audience taxonomy: `user_facing`, `agent_facing`, `machine`; запрет implicit `mixed`.
- [x] P0.30 Описать effective-value provenance, independent migrations, atomic writes и правила session overrides.

### Gate этапа 0

- [x] Все обязательные ADR приняты.
- [x] State machines не содержат неразрешённых переходов.
- [x] Все внешние границы имеют versioned schema.
- [x] Для каждой публичной capability определены CLI/TUI entrypoint, MAUI action, permission policy и acceptance test.
- [x] Для каждой recommendation определены rationale, preconditions, safety class, target и stale-state behavior.
- [x] Определены локализуемые surfaces и invariant machine-facing contracts.
- [x] Каждый configuration key имеет единственный owner scope, schema, default и provenance.
- [x] Каждый generator объявляет artifact audience и language-resolution contract.
- [x] Для каждого destructive/mutating действия определены rollback или recovery.
- [x] Scope MVP и deferred backlog явно разделены.

---

## Этап 1. Каркас .NET solution

**Зависит от:** этапа 0.

### Структура

- [ ] P1.1 Создать `Forge.sln`.
- [ ] P1.2 Создать `Forge.Cli`.
- [ ] P1.3 Создать `Forge.Bootstrap`.
- [ ] P1.4 Создать platform-neutral `Forge.Updater`.
- [ ] P1.5 Создать `Forge.Updater.Windows`.
- [ ] P1.6 Создать `Forge.Domain`.
- [ ] P1.7 Создать `Forge.Application`.
- [ ] P1.8 Создать `Forge.Infrastructure`.
- [ ] P1.9 Создать `Forge.Providers`.
- [ ] P1.10 Создать unit, integration, acceptance и installer test projects.
- [ ] P1.19 Создать `Forge.Presentation` с immutable DTO, commands, queries, typed events и capability IDs.
- [ ] P1.20 Создать `Forge.Desktop` как .NET MAUI Windows application.

### Базовая инфраструктура

- [ ] P1.11 Настроить dependency injection и composition root.
- [ ] P1.12 Добавить abstractions для clock, filesystem, process runner, network client и environment.
- [ ] P1.13 Настроить structured logging и redaction pipeline.
- [ ] P1.14 Настроить nullable reference types, analyzers, formatting и warnings-as-errors.
- [ ] P1.15 Настроить unit/integration test categories.
- [ ] P1.16 Настроить CI для build, format, unit tests и Windows integration tests.
- [ ] P1.17 Добавить architecture tests на направления зависимостей assemblies.
- [ ] P1.18 Создать CLI skeleton с `--version`, `doctor` и внутренним installer self-test.
- [ ] P1.21 Создать MAUI skeleton с global status, выбором project root и внутренним installer self-test.
- [ ] P1.22 Настроить Windows MAUI build/test/publish в CI для `win-x64` и `win-arm64`.
- [ ] P1.23 Запретить прямые ссылки CLI/Desktop на Infrastructure, Providers, Git, SQLite и updater implementations.
- [ ] P1.24 Создать `Forge.Localization` с `ILocalizationCatalog`, culture resolver, formatter и English fallback.
- [ ] P1.25 Создать полные English и Russian resource catalogs.
- [ ] P1.26 Добавить catalog linter для missing/unused keys, placeholder и plural/select compatibility.
- [ ] P1.27 Добавить analyzer/test, запрещающий hard-coded user-facing strings в CLI/TUI и MAUI.
- [ ] P1.28 Создать `Forge.Configuration` со scope/schema registry и effective-value provenance.
- [ ] P1.29 Реализовать интерфейсы независимых user/project config stores.
- [ ] P1.30 Реализовать versioned migrations и atomic-write abstraction отдельно для каждого scope.
- [ ] P1.31 Добавить architecture tests, запрещающие обход scoped configuration API.

### Gate этапа 1

- [ ] `dotnet restore`, `dotnet build` и `dotnet test` проходят в чистом checkout.
- [ ] `Forge.Updater` не ссылается на `Forge.Updater.Windows`.
- [ ] Infrastructure и providers подключаются только через interfaces.
- [ ] CLI/TUI и MAUI hosts собираются, запускаются и используют один application/presentation contract.
- [ ] Оба hosts используют один localization catalog; `en` и `ru` проходят completeness checks.
- [ ] User/project config schemas валидируются независимо и отклоняют wrong-scope keys.
- [ ] Architecture tests подтверждают отсутствие business orchestration в presentation projects.
- [ ] Logs проходят базовые redaction tests.
- [ ] CI воспроизводит локальные quality gates.

---

## Этап 2. Кросс-платформенное ядро self-updater

**Зависит от:** этапов 0–1.

### Platform-neutral модель

- [ ] P2.1 Реализовать определение OS family, OS architecture и process architecture через runtime API.
- [ ] P2.2 Реализовать нормализованный `UpdateTarget` (`os`, `architecture`, `packaging`).
- [ ] P2.3 Реализовать registry/resolver для `IPlatformUpdateStrategy`.
- [ ] P2.4 Реализовать явный результат `platform_not_supported`.
- [ ] P2.5 Запретить network/filesystem mutation до успешного выбора стратегии.

### GitHub Releases

- [ ] P2.6 Реализовать client для latest published full release.
- [ ] P2.7 Реализовать draft/prerelease filtering и SemVer comparison.
- [ ] P2.8 Реализовать запрет downgrade.
- [ ] P2.9 Реализовать conditional request с `ETag` без TTL bypass.
- [ ] P2.10 Реализовать выбор release asset по нормализованному target.
- [ ] P2.11 Реализовать проверку имени, размера и SHA-256 asset.
- [ ] P2.12 Реализовать signature/provenance verifier и встроенную trust policy.

### State machine

- [ ] P2.13 Реализовать состояния `current`, `downloading`, `verifying`, `staging`, `activating`, `restarting`, `rolled_back`, `failed`.
- [ ] P2.14 Реализовать restart token и защиту от update loop.
- [ ] P2.15 Реализовать сохранение исходных аргументов без shell re-parsing.
- [ ] P2.16 Реализовать сохранение current working directory.
- [ ] P2.17 Реализовать startup handshake contract.
- [ ] P2.18 Реализовать platform-neutral orchestration rollback.

### Тесты

- [ ] P2.19 Покрыть OS/architecture detection matrix.
- [ ] P2.20 Покрыть fake Windows/Linux/macOS strategies contract tests.
- [ ] P2.21 Проверить, что Linux/macOS без зарегистрированной стратегии возвращают `platform_not_supported`.
- [ ] P2.22 Проверить current, upgrade, downgrade, malformed release и missing asset.
- [ ] P2.23 Проверить checksum/signature mismatch.
- [ ] P2.24 Проверить сохранение arguments/cwd и защиту от restart loop.

### Gate этапа 2

- [ ] Общий updater не содержит Windows paths, mutex, PowerShell или locked-file logic.
- [ ] Добавление fake platform strategy не требует изменения общего state machine.
- [ ] Неподдерживаемая платформа не выполняет network/filesystem mutation.
- [ ] Все failure paths оставляют active version неизменной.

---

## Этап 3. Windows installer и Windows update strategy

**Зависит от:** этапа 2.

### Windows installer

- [ ] P3.1 Создать `scripts/install.ps1`.
- [ ] P3.2 Реализовать определение `win-x64` и `win-arm64`.
- [ ] P3.3 Реализовать загрузку release asset, checksum и signature/provenance.
- [ ] P3.4 Реализовать per-user versioned layout в `%LOCALAPPDATA%\Programs\Forge\`.
- [ ] P3.5 Реализовать internal self-test до активации.
- [ ] P3.6 Реализовать идемпотентное добавление Forge в пользовательский `PATH`.
- [ ] P3.7 Реализовать сохранение предыдущей версии и installer rollback.
- [ ] P3.8 Реализовать результат `forge_installed_toolchain_not_ready`.
- [ ] P3.24 Упаковать CLI/TUI, MAUI Desktop и updater helper в один versioned release bundle.
- [ ] P3.25 Создать и идемпотентно обновлять Start Menu shortcut для MAUI Desktop.
- [ ] P3.26 Выполнять internal host self-tests для CLI и Desktop до активации bundle.
- [ ] P3.29 Включить `en`/`ru` catalogs и локализованные installer-facing messages; `install.ps1 -Language en|ru` с default `en` инициализирует три user language keys.

### `WindowsUpdateStrategy`

- [ ] P3.9 Реализовать named mutex и bounded wait.
- [ ] P3.10 Реализовать Windows install layout и staging directory.
- [ ] P3.11 Реализовать внешний activation/restart helper.
- [ ] P3.12 Реализовать ожидание завершения parent process.
- [ ] P3.13 Реализовать atomic switch `current`.
- [ ] P3.14 Реализовать запуск новой версии и ожидание startup handshake.
- [ ] P3.15 Реализовать rollback при timeout/crash/invalid handshake.
- [ ] P3.16 Реализовать безопасную обработку двух одновременных запусков.

### Acceptance tests

- [ ] P3.17 Установка в чистый Windows user profile без elevation.
- [ ] P3.18 Повторная установка не дублирует `PATH`.
- [ ] P3.19 Обновление N → N+1 продолжает исходную команду.
- [ ] P3.20 Arguments и working directory сохраняются после restart.
- [ ] P3.21 Повреждённый asset не меняет active version.
- [ ] P3.22 Отсутствующий handshake откатывает версию.
- [ ] P3.23 Параллельные процессы не скачивают один release дважды.
- [ ] P3.27 Start Menu shortcut запускает Desktop из активной версии.
- [ ] P3.28 Self-update активирует CLI/TUI и MAUI Desktop одной версии и восстанавливает исходный surface.
- [ ] P3.30 Install/update/rollback сохраняют per-user language setting и не смешивают catalogs разных версий.

### Gate этапа 3

- [ ] `forge` доступен по имени в новой консоли.
- [ ] Self-update N → N+1 проходит end-to-end.
- [ ] Rollback подтверждён acceptance test.
- [ ] Installer и updater не требуют administrator privileges.
- [ ] CLI/TUI, Desktop и updater устанавливаются и откатываются атомарно как один bundle.
- [ ] Windows-specific код находится только в Windows assembly/scripts.

---

## Этап 4. Startup pipeline и инициализация `.forge/`

**Зависит от:** этапов 1–3.

### Startup pipeline

- [ ] P4.1 Реализовать порядок: user config/culture → self-update → provider preflight → project root → project config → command.
- [ ] P4.2 Сделать `--version` и `doctor --startup` независимыми от project root.
- [ ] P4.3 Ограничить bypass self-update внутренним installer self-test.
- [ ] P4.4 Реализовать recovery-only mode после startup failure.
- [ ] P4.17 Подключить CLI/TUI и MAUI к одному shared bootstrap pipeline.
- [ ] P4.18 В Desktop реализовать выбор каталога и список последних проектов.
- [ ] P4.19 Использовать в Desktop тот же project-root resolver и atomic init flow, что и в CLI.
- [ ] P4.20 Сохранять и восстанавливать MAUI navigation intent после self-update/restart.
- [ ] P4.21 После project bootstrap запрашивать status snapshot и next actions до показа основного UI.
- [ ] P4.22 Bare `forge` должен выводить project status, active/attention sprint status и 1–5 готовых следующих команд.
- [ ] P4.23 Desktop после выбора проекта должен открывать status dashboard с recommended actions.
- [ ] P4.24 При startup failure показывать ограниченный recovery snapshot и безопасные remediation actions без обхода fail-closed policy.
- [ ] P4.25 До первого user-facing startup message загружать/migrate user config и разрешать UI/interaction/LLM languages.
- [ ] P4.26 Реализовать Windows user config `%LOCALAPPDATA%\Forge\config.yaml` через OS-neutral store.
- [ ] P4.27 Реализовать `forge config user set language.ui|interaction|llm <culture>` и соответствующие session overrides.
- [ ] P4.28 Реализовать MAUI selectors для UI/interaction/LLM languages и немедленное обновление применимых surfaces.
- [ ] P4.29 Загружать project config только после project-root verification и только из `.forge/`.
- [ ] P4.30 Реализовать `forge config user|project get/set/list/validate`.
- [ ] P4.31 Реализовать `forge config show --effective --provenance` и общий MAUI effective-config screen.
- [ ] P4.32 Отклонять user key в project config и project key в user config как `configuration_scope_violation`.
- [ ] P4.33 Выполнять независимые atomic migrations user config и project manifest с отдельным rollback/recovery.
- [ ] P4.34 Инициализировать project artifact languages явно как `en`/`en`, не копируя user languages.

### Project root

- [ ] P4.5 Проверять только current directory или явный абсолютный `--project-root`.
- [ ] P4.6 Не выполнять автоматический upward search при инициализации.
- [ ] P4.7 При отсутствии `.forge/manifest.yaml` показывать абсолютный путь и запрашивать подтверждение корня.
- [ ] P4.8 Ответ `no` должен завершать процесс без filesystem changes.
- [ ] P4.9 В non-interactive mode требовать `forge init --project-root <path> --yes`.

### Инициализация

- [ ] P4.10 Создавать `.forge/` через staging directory и atomic publish.
- [ ] P4.11 Создать минимальный `manifest.yaml` и новый `project_id`.
- [ ] P4.12 Создать `constitution.md`, rules, agents, skills, workflows, schemas, policies, specs и decisions.
- [ ] P4.13 Создать только `workflows/implementation-critical.yaml`.
- [ ] P4.14 Создать `.forge/.gitignore` для state, build, logs и temporary files.
- [ ] P4.15 Не перезаписывать существующую или неизвестную `.forge/`.
- [ ] P4.16 Валидировать результат инициализации до запуска project command.

### Gate этапа 4

- [ ] Bare `forge` в новом проекте выполняет confirm → init → validate → UI.
- [ ] Desktop выполняет select root → confirm → init → validate с теми же результатами и diagnostics.
- [ ] После self-update открывается тот же Desktop project/view либо продолжается исходная CLI-команда.
- [ ] При каждом интерактивном запуске видны актуальный project/sprint status и хотя бы одно допустимое действие либо явное объяснение, почему действий нет.
- [ ] Чистая установка использует English; выбранный Russian применяется одинаково в CLI/TUI и MAUI и сохраняется между запусками.
- [ ] Неизвестная culture безопасно использует English fallback с диагностикой.
- [ ] Смена project root не меняет user UI/interaction/LLM languages.
- [ ] Project init записывает явные user-facing/agent-facing artifact languages и не наследует их из user config.
- [ ] Effective config показывает scope и source; wrong-scope key блокирует startup/config mutation с понятной диагностикой.
- [ ] Повторный запуск не повторяет init.
- [ ] Отказ пользователя не оставляет новых файлов.
- [ ] Crash injection не оставляет частично опубликованную `.forge/`.
- [ ] Вне `.forge/` нет канонической project-scope forge-конфигурации; user-scope существует только в OS user config directory.

---

## Этап 5. Provider toolchain updater и adapters

**Зависит от:** этапов 1, 3–4.

### Общий toolchain manager

- [ ] P5.1 Описать provider discovery/update strategy contract.
- [ ] P5.2 Реализовать Windows executable discovery для `codex` и `claude`.
- [ ] P5.3 Реализовать безопасный local version/help check без model call.
- [ ] P5.4 Реализовать latest-version lookup из утверждённых official channels.
- [ ] P5.5 Реализовать определение installation method.
- [ ] P5.6 Реализовать install/update strategy для отсутствующей или устаревшей CLI.
- [ ] P5.7 После update сбрасывать path cache и повторять discovery/version/help.
- [ ] P5.8 Запретить project `.forge/` переопределять update URL или command.
- [ ] P5.9 Блокировать sprint-команды, если хотя бы одна CLI не перешла в `ready`.

### Provider adapters

- [ ] P5.10 Реализовать запуск Claude Code CLI без shell string concatenation.
- [ ] P5.11 Реализовать запуск Codex CLI без shell string concatenation.
- [ ] P5.12 Реализовать JSON/JSONL event parsing.
- [ ] P5.13 Реализовать schema-constrained output validation.
- [ ] P5.14 Реализовать version/capability compatibility checks.
- [ ] P5.15 Реализовать auth diagnostics без чтения OAuth credentials.
- [ ] P5.16 Реализовать error normalization: quota, rate limit, auth, policy, transient, malformed output.
- [ ] P5.17 Реализовать fixture tests для stdout/stderr/events разных версий.

### Gate этапа 5

- [ ] Missing CLI проходит install → recheck → ready.
- [ ] Outdated CLI проходит update → recheck → ready.
- [ ] Failed update оставляет доступными только doctor/recovery.
- [ ] Одна read-only задача выполняется обоими providers по одной output schema.
- [ ] Credentials и полный environment не попадают в logs.

---

## Этап 6. Независимые спринты и durable workflow engine

**Зависит от:** этапов 0–5.

### Sprint model

- [ ] P6.1 Реализовать `Sprint`, `Node`, `Attempt`, `Event` и их state machines.
- [ ] P6.2 Зафиксировать input snapshot, `base SHA`, workflow version и policy snapshot.
- [ ] P6.3 Реализовать namespace по `sprint_id`.
- [ ] P6.4 Реализовать SQLite migrations и WAL.
- [ ] P6.5 Реализовать content-addressed artifact store.
- [ ] P6.6 Реализовать append-only event recording.

### Workflow runtime

- [ ] P6.7 Реализовать deterministic, LLM, human gate, map, parallel, pipeline, router, barrier, loop, Git и validation nodes.
- [ ] P6.8 Реализовать typed inputs/outputs и schema validation.
- [ ] P6.9 Реализовать dependency resolution и runnable-node scheduler.
- [ ] P6.10 Реализовать bounded concurrency.
- [ ] P6.11 Реализовать timeout, retry budget и idempotency key.
- [ ] P6.12 Реализовать bounded loops и convergence conditions.
- [ ] P6.13 Реализовать crash-safe resume без повторения completed nodes.
- [ ] P6.14 Реализовать cancel отдельного спринта.

### Application API и CLI

- [ ] P6.15 Реализовать `forge sprint create`.
- [ ] P6.16 Реализовать `forge sprint list/status/inspect`.
- [ ] P6.17 Реализовать `forge sprint run/resume/cancel`.
- [ ] P6.18 Реализовать единый application command/query API для sprint lifecycle.
- [ ] P6.19 Реализовать typed live event subscription с восстановлением позиции.
- [ ] P6.20 Реализовать optimistic concurrency и idempotency между одновременно открытыми surfaces.
- [ ] P6.21 Реализовать общие presentation DTO без утечки domain/infrastructure objects.
- [ ] P6.22 Реализовать `ProjectStatusQuery`, агрегирующий `.forge/`, Git, provider diagnostics, sprint/gate/finding state.
- [ ] P6.23 Реализовать versioned deterministic `NextActionAdvisor`.
- [ ] P6.24 Реализовать `SuggestedAction` с rationale, preconditions, command/UI target, safety class, priority и expected state version.
- [ ] P6.25 Реализовать выбор active sprint: explicit selection → единственный non-terminal; при нескольких — overview без молчаливого выбора.
- [ ] P6.26 Пересчитывать snapshot/recommendations после state-changing commands и значимых domain events.
- [ ] P6.27 Отклонять устаревшую рекомендацию как `suggestion_stale`, обновлять snapshot и не выполнять side effect.
- [ ] P6.28 Реализовать `forge status|next` и их machine-readable `--json` формы без интерактивного текста в stdout.
- [ ] P6.29 Сохранять в events/findings/diagnostics `message_key` и typed arguments, а не rendered localized string.
- [ ] P6.30 Сделать JSON output, event/diagnostic codes и state serialization invariant при смене culture.
- [ ] P6.31 Фиксировать project configuration snapshot, artifact language policy и provenance во входах спринта.
- [ ] P6.32 Передавать provider invocation раздельные `conversation_language`, `artifact_output_language` и artifact audience.
- [ ] P6.33 Не передавать language instruction для schema-constrained `machine` artifacts.
- [ ] P6.34 Включать effective `language.llm` в attempt context manifest/input hash, не включая presentation-only UI/interaction languages.

### Gate этапа 6

- [ ] Принудительно завершённый процесс корректно продолжает спринт.
- [ ] Invalid transition отвергается.
- [ ] Loop cap и retry budget работают.
- [ ] Два спринта имеют раздельные state/events/artifacts.
- [ ] Cancel одного спринта не меняет второй.
- [ ] CLI/TUI и MAUI одновременно наблюдают одно durable sprint state без lost updates и повторных side effects.
- [ ] Одинаковый snapshot даёт одинаковый упорядоченный список рекомендаций.
- [ ] Suggested mutating/destructive action проходит обычные permission и confirmation checks.
- [ ] Один persisted event корректно отображается на English и Russian без изменения durable state.
- [ ] Смена user language во время спринта не меняет зафиксированный project artifact language snapshot.

---

## Этап 7. Git isolation, safe fallback и circuit breaker

**Зависит от:** этапов 5–6.

### Git/worktree

- [ ] P7.1 Реализовать integration branch/worktree на каждый спринт.
- [ ] P7.2 Реализовать отдельный worktree на каждую write-attempt.
- [ ] P7.3 Реализовать фиксацию и проверку `base SHA`.
- [ ] P7.4 Реализовать dirty-state detection и recovery artifacts.
- [ ] P7.5 Реализовать disjoint write scopes и ownership checks.
- [ ] P7.6 Реализовать barrier перед интеграцией.
- [ ] P7.7 Реализовать `forge sprint rebase` с повтором затронутых gates.

### Routing/fallback

- [ ] P7.8 Реализовать provider/model/surface health keys.
- [ ] P7.9 Реализовать circuit breaker, cooldown и reset.
- [ ] P7.10 Реализовать общий wall-clock/attempt retry budget.
- [ ] P7.11 Реализовать clean replay write-node из исходного `base SHA`.
- [ ] P7.12 Запретить продолжение fallback поверх неизвестного diff.
- [ ] P7.13 Не маскировать authentication/policy failure fallback-механизмом.
- [ ] P7.14 Логировать route decision и причины.

### Gate этапа 7

- [ ] Synthetic quota event переключает provider.
- [ ] Partial changes первой попытки не попадают во вторую.
- [ ] Retry amplification отсутствует.
- [ ] Authentication failure не классифицируется как transient.
- [ ] Integration конфликт эскалируется, а не разрешается LLM молча.

---

## Этап 8. `.forge/` SSoT compiler

**Зависит от:** этапов 4–6.

### Parser и IR

- [ ] P8.1 Реализовать загрузку `.forge/manifest.yaml`.
- [ ] P8.2 Разрешать относительные пути только относительно `.forge/`.
- [ ] P8.3 Реализовать YAML/Markdown/frontmatter validation.
- [ ] P8.4 Реализовать semantic IR для rules, agents, skills, workflows и policies.
- [ ] P8.5 Реализовать ID/reference validation.
- [ ] P8.6 Реализовать inheritance и path scopes.
- [ ] P8.7 Реализовать context-size limits.

### Generators

- [ ] P8.8 Реализовать Claude-native generator.
- [ ] P8.9 Реализовать Codex-native generator.
- [ ] P8.10 Маркировать generated files source hash и generator version.
- [ ] P8.11 Реализовать `.forge/build/manifest.json`.
- [ ] P8.12 Реализовать drift detection.
- [ ] P8.13 Реализовать `forge sync`, `sync --check`, `validate` и `check-drift`.
- [ ] P8.14 Запретить generated provider files становиться каноническим источником.
- [ ] P8.15 Парсить и валидировать `artifacts.language.user_facing|agent_facing` из project manifest.
- [ ] P8.16 Создать registry artifact generators с обязательным audience `user_facing|agent_facing|machine`.
- [ ] P8.17 Разрешать artifact template/terminology по project language и language-pack capability.
- [ ] P8.18 Блокировать generation при unknown audience или отсутствии artifact language capability без silent fallback.
- [ ] P8.19 Записывать audience, language, project config snapshot hash и generator version в artifact metadata.
- [ ] P8.20 Для двух аудиторий создавать два явно типизированных representations вместо `mixed`.

### Gate этапа 8

- [ ] Один canonical agent воспроизводимо генерирует Claude/Codex definitions.
- [ ] Ручная правка generated file обнаруживается.
- [ ] Context limit violation блокирует build.
- [ ] Повторная компиляция одинакового input даёт одинаковые hashes.
- [ ] User-facing и agent-facing fixtures генерируются на независимо заданных project languages.
- [ ] Два разных user configs разрешают одинаковые artifact audience/language/template policies из одного project/sprint snapshot.
- [ ] Machine artifacts остаются byte/schema invariant при смене language settings.
- [ ] В project root нет второго forge configuration source.

---

## Этап 9. Memory, context builder и code intelligence

**Зависит от:** этапов 6–8.

### Memory/context

- [ ] P9.1 Реализовать L0 always-on core.
- [ ] P9.2 Реализовать sprint-scoped L1 memory.
- [ ] P9.3 Реализовать structured handoff validation.
- [ ] P9.4 Реализовать project knowledge и ADR/spec lookup.
- [ ] P9.5 Реализовать reproducible context manifest.
- [ ] P9.6 Реализовать token budget и progressive disclosure.
- [ ] P9.7 Реализовать BM25/full-text lookup.
- [ ] P9.8 Не использовать raw transcript как обязательную память.
- [ ] P9.9 Реализовать retention и CAS reachability cleanup.

### Code intelligence

- [ ] P9.10 Реализовать Git/files/ripgrep query layer.
- [ ] P9.11 Реализовать Tree-sitter outline.
- [ ] P9.12 Реализовать LSP/Serena integration abstraction.
- [ ] P9.13 Реализовать optional graph/SCIP adapter.
- [ ] P9.14 Реализовать stale-index detection по SHA/tool versions.
- [ ] P9.15 Реализовать graceful fallback без graph index.
- [ ] P9.16 Требовать file/LSP evidence для critical references.

### Gate этапа 9

- [ ] Provider заменяется без передачи transcript.
- [ ] Один context manifest воспроизводит selection.
- [ ] Большие outputs не инъецируются целиком без причины.
- [ ] Система корректно работает без graph index.
- [ ] Stale index обнаруживается и не используется как единственное evidence.

---

## Этап 10. Workflow `implementation-critical`

**Зависит от:** этапов 5–9.

### Workflow

- [ ] P10.1 Реализовать intake.
- [ ] P10.2 Реализовать risk/threat analysis.
- [ ] P10.3 Реализовать spec и acceptance criteria.
- [ ] P10.4 Реализовать architecture alternatives и ADR.
- [ ] P10.5 Реализовать traceability.
- [ ] P10.6 Реализовать plan → task DAG.
- [ ] P10.7 Реализовать isolated implementation fan-out.
- [ ] P10.8 Реализовать complete test matrix.
- [ ] P10.9 Реализовать correctness и security review.
- [ ] P10.10 Реализовать cross-provider independence rules.
- [ ] P10.11 Реализовать review convergence и finding deduplication.
- [ ] P10.12 Реализовать adversarial verification.
- [ ] P10.13 Реализовать mandatory human approval.
- [ ] P10.14 Реализовать finalize sprint/PR.

### Роли, gates и interaction surfaces

- [ ] P10.15 Реализовать Analyst, Architect, Implementer, Test Engineer, Correctness Reviewer, Security Reviewer и Integrator.
- [ ] P10.16 Реализовать format/build/unit/integration/secrets gates.
- [ ] P10.17 Требовать двух независимых reviewers.
- [ ] P10.18 Ограничить review loop.
- [ ] P10.19 Запретить выбор `fast` и `standard` через CLI/manifest.
- [ ] P10.20 Реализовать полный CLI/TUI surface: project init, sprint lifecycle, DAG, events, findings, gates, artifacts, approvals, recovery, diagnostics, sync, validate и evals.
- [ ] P10.21 Реализовать MAUI project picker, recent projects и project dashboard.
- [ ] P10.22 Реализовать в MAUI create/run/resume/cancel/rebase и список спринтов.
- [ ] P10.23 Реализовать в MAUI интерактивный DAG, node details и live event stream.
- [ ] P10.24 Реализовать в MAUI findings, gates, artifacts, diffs и logs.
- [ ] P10.25 Реализовать в MAUI human approvals, permission prompts и recovery actions.
- [ ] P10.26 Реализовать в MAUI diagnostics, provider/update status, sync, validate и evals.
- [ ] P10.27 Заполнить versioned capability matrix для всех публичных возможностей.
- [ ] P10.28 Создать автоматический parity acceptance test для каждой строки capability matrix.
- [ ] P10.29 Проверить одновременную работу CLI/TUI и MAUI с одним спринтом.
- [ ] P10.30 Закрытие Desktop window не помечает durable sprint отменённым; повторный запуск восстанавливает UI из SQLite/CAS и event stream.
- [ ] P10.31 Реализовать в CLI/TUI блоки «Статус проекта», «Спринт требует внимания» и «Что делать дальше» с numbered commands.
- [ ] P10.32 Реализовать в MAUI status cards и recommended actions с rationale, navigation target и confirmation.
- [ ] P10.33 Обновлять guidance в обоих surfaces после command completion, gate/finding/error и human action.
- [ ] P10.34 Обеспечить одинаковый порядок и смысл рекомендаций в CLI/TUI и MAUI для одной snapshot version.
- [ ] P10.35 Локализовать на English/Russian CLI/TUI и MAUI: menus, help, prompts, diagnostics, status, guidance, approvals и recovery.
- [ ] P10.36 Локализовать suggested-action title/rationale, сохранив command preview и identifiers invariant.
- [ ] P10.37 Проверить немедленное переключение языка без изменения workflow/sprint state.
- [ ] P10.38 Добавить language settings capability в CLI/TUI ↔ MAUI capability matrix.
- [ ] P10.39 Реализовать CLI/TUI user/project config editor и effective/provenance view.
- [ ] P10.40 Реализовать MAUI раздельные User Settings и Project Settings screens.
- [ ] P10.41 Показывать artifact audience/language и source policy в preview/details до generation/regeneration.
- [ ] P10.42 Показывать отдельно текущие UI/interaction/LLM languages и project user-facing/agent-facing languages.

### Gate этапа 10

- [ ] Полный representative change проходит workflow end-to-end.
- [ ] Security reviewer участвует в каждом спринте.
- [ ] Human approval невозможно обойти.
- [ ] CLI/TUI и MAUI имеют полный функциональный паритет и одинаковые permission/human-gate semantics.
- [ ] В одном surface нет скрытых public capabilities, отсутствующих во втором.
- [ ] Действие из одного surface наблюдается во втором без restart и без повторного side effect.
- [ ] После запуска и каждого значимого перехода оба surfaces объясняют состояние и предлагают следующий допустимый шаг.
- [ ] При нескольких non-terminal спринтах Forge показывает attention overview и предлагает явный выбор, не назначая active sprint молча.
- [ ] Все публичные user-facing flows доступны на English и Russian с одинаковой семантикой и полным fallback.
- [ ] Оба surfaces явно разделяют user/project settings и не предлагают записать key в неправильный scope.
- [ ] LLM conversation language и artifact output language могут различаться без неоднозначности в UI.
- [ ] DoD подтверждается реальными exit codes и artifacts.
- [ ] В MVP существует ровно один selectable implementation workflow.

---

## Этап 11. Наблюдаемость, evals и security hardening

**Зависит от:** этапов 2–10.

### Наблюдаемость

- [ ] P11.1 Добавить startup/self-update/provider/sprint OpenTelemetry spans.
- [ ] P11.2 Добавить local/latest versions и selected platform strategy в diagnostics.
- [ ] P11.3 Добавить metrics для completion, fallback, retry, context, updates и reviews.
- [ ] P11.4 Реализовать safe diagnostic bundles.
- [ ] P11.5 Проверить отсутствие secrets, tokens и full environment в logs.

### Evals

- [ ] P11.6 Создать updater platform/upgrade/rollback/concurrency suite.
- [ ] P11.7 Создать provider quota/auth/malformed-output suite.
- [ ] P11.8 Создать project-init/crash/idempotency suite.
- [ ] P11.9 Создать sprint isolation/resume/cancel suite.
- [ ] P11.10 Создать representative implementation task suite.
- [ ] P11.11 Определить regression thresholds.
- [ ] P11.12 Обновлять model policies только через eval results.

### Security

- [ ] P11.13 Провести threat-model review.
- [ ] P11.14 Проверить release trust chain.
- [ ] P11.15 Проверить prompt-injection boundaries.
- [ ] P11.16 Проверить permission policies и sandbox defaults.
- [ ] P11.17 Проверить supply-chain pinning для skills/MCP.
- [ ] P11.18 Провести dependency/license/vulnerability scan.
- [ ] P11.19 Добавить CLI/TUI ↔ MAUI capability parity, navigation restore и concurrent-surface suite.
- [ ] P11.20 Добавить architecture check, блокирующий UI-specific business logic и drift capability matrix.
- [ ] P11.21 Добавить status/advisor suite: no sprint, one sprint, multiple sprints, awaiting human, blocked, failed, resumable и ready-to-finalize.
- [ ] P11.22 Добавить stale-suggestion/concurrent-update suite и проверку отсутствия side effects.
- [ ] P11.23 Добавить invariants/property tests для стабильного ranking и приоритетов safety/attention.
- [ ] P11.24 Добавить metrics/traces для snapshot build, recommendation impression/selection/stale и time-to-next-action.
- [ ] P11.25 Добавить localization completeness/placeholder/plural/fallback suite для `en` и `ru`.
- [ ] P11.26 Добавить pseudo-localization, Unicode, long-string и TUI/MAUI layout tests.
- [ ] P11.27 Подключить synthetic third-language catalog без изменений Domain/Application/workflow code.
- [ ] P11.28 Проверить invariant commands, JSON, codes, telemetry и persisted state при смене culture.
- [ ] P11.29 Добавить localization resolve/fallback/missing-key metrics без утечки localized user content.
- [ ] P11.30 Добавить scoped-config schema/wrong-scope/provenance/default suite.
- [ ] P11.31 Добавить independent user/project migration, concurrent-write и crash-recovery suite.
- [ ] P11.32 Добавить cross-user policy-resolution suite с разными user languages и одним project snapshot.
- [ ] P11.33 Добавить LLM conversation `ru` + agent-facing artifact `en` acceptance scenario.
- [ ] P11.34 Добавить раздельную генерацию user-facing `ru` и agent-facing `en`, включая metadata.
- [ ] P11.35 Проверить блокировку generation при missing artifact-language capability.

### Gate этапа 11

- [ ] Все critical security findings закрыты.
- [ ] Evals проходят установленные thresholds.
- [ ] Diagnostic bundle не содержит secrets.
- [ ] Parity suite не содержит missing capabilities или различий permission semantics.
- [ ] Advisor suite подтверждает одинаковые recommendations в обоих surfaces и отсутствие автоматического mutating action.
- [ ] Localization suite подтверждает полный `en`/`ru`, English fallback и подключение дополнительного test language pack.
- [ ] Scoped-config suite подтверждает отсутствие cross-scope override и зависимостей project artifacts от user config.
- [ ] Model/provider update не проходит без compatibility suite.
- [ ] Self-update не активирует неподписанный или повреждённый asset.

---

## Этап 12. Сборка и приёмка MVP

**Зависит от:** всех предыдущих этапов.

### Release engineering

- [ ] P12.1 Настроить reproducible publish единого CLI/TUI + MAUI Desktop + updater bundle для `win-x64` и `win-arm64`.
- [ ] P12.2 Создать GitHub Release assets по принятой naming scheme.
- [ ] P12.3 Опубликовать checksum manifest и signature/provenance.
- [ ] P12.4 Создать SBOM.
- [ ] P12.5 Проверить installer на опубликованных assets, а не локальных файлах.
- [ ] P12.6 Проверить self-update с предыдущего release candidate.
- [ ] P12.7 Проверить migration/rollback sprint state.
- [ ] P12.8 Подготовить install, recovery, doctor и troubleshooting documentation.

### End-to-end acceptance

- [ ] P12.9 Чистый Windows profile → `install.ps1` → новая консоль → `forge`.
- [ ] P12.10 Forge self-update N → N+1 → исходная команда продолжается.
- [ ] P12.11 Missing/outdated Codex и Claude → install/update → recheck → ready.
- [ ] P12.12 Новый project root → confirm → `.forge/` init → validate.
- [ ] P12.13 Два независимых спринта выполняются без общего mutable state.
- [ ] P12.14 Quota fallback выполняет clean replay.
- [ ] P12.15 `implementation-critical` проходит до human approval и финализации.
- [ ] P12.16 Unavailable GitHub/provider latest lookup блокирует project/sprint work с корректной диагностикой.
- [ ] P12.17 Чистый Windows profile → installer → Start Menu → Forge Desktop.
- [ ] P12.18 Один representative workflow полностью выполняется отдельно через CLI/TUI и MAUI с эквивалентным durable result.
- [ ] P12.19 Изменения sprint state из CLI немедленно видны в MAUI и наоборот.
- [ ] P12.20 Self-update N → N+1 сохраняет единую версию обоих hosts и корректно восстанавливает исходный surface.
- [ ] P12.21 Approval и permission decision имеют одинаковый результат в CLI/TUI и MAUI.
- [ ] P12.22 Закрытие и повторный запуск Desktop восстанавливает выбранный проект, sprint view и durable state.
- [ ] P12.23 Проект без спринтов → status snapshot → рекомендация создать `implementation-critical` sprint.
- [ ] P12.24 Sprint на human gate → запуск Forge → gate показан первым и предложено безопасное approval/review action.
- [ ] P12.25 Несколько non-terminal спринтов → attention overview → явный выбор без скрытого переключения active sprint.
- [ ] P12.26 Concurrent state change делает recommendation stale → action отклоняется → UI обновляется без side effect.
- [ ] P12.27 Один snapshot в CLI/TUI и MAUI даёт эквивалентные status и ordered next actions.
- [ ] P12.28 `forge status --json` и `forge next --json` проходят schema validation и пригодны для automation.
- [ ] P12.29 Чистая установка → CLI/TUI и MAUI запускаются на English.
- [ ] P12.30 Переключение на Russian → оба surfaces, startup status, prompts и guidance отображаются по-русски.
- [ ] P12.31 Language setting сохраняется после закрытия, self-update, rollback и повторного запуска.
- [ ] P12.32 Смена `en` ↔ `ru` не меняет JSON output, event codes или durable sprint state.
- [ ] P12.33 Missing translation key/unknown culture → English fallback, diagnostic event и работоспособный UI.
- [ ] P12.34 Новый synthetic language pack подключается без изменения application/workflow assemblies.
- [ ] P12.35 User config `ui=ru`, `interaction=ru`, `llm=ru` сохраняется при переходе между проектами.
- [ ] P12.36 Project config `user_facing=ru`, `agent_facing=en` применяется одинаково для двух пользователей с разными user configs.
- [ ] P12.37 LLM отвечает пользователю по-русски и создаёт agent-facing artifact по-английски в одной операции.
- [ ] P12.38 Machine artifact и durable state остаются invariant при всех комбинациях user/project languages.
- [ ] P12.39 Wrong-scope key блокируется с `configuration_scope_violation` и точной provenance diagnostic.
- [ ] P12.40 `forge config show --effective --provenance` совпадает с MAUI effective-config view.
- [ ] P12.41 Изменение project artifact language не переписывает старые artifacts без explicit regeneration.
- [ ] P12.42 Отсутствующая artifact language capability блокирует generation без English silent fallback.

### Финальный gate MVP

- [ ] Все сводные этапы 0–12 отмечены выполненными.
- [ ] Нет открытых blocker/high findings.
- [ ] Все acceptance scenarios воспроизводимы из clean environment.
- [ ] Архитектурный документ соответствует реализованной системе.
- [ ] Известные ограничения MVP документированы.
- [ ] Все строки capability matrix реализованы и приняты для CLI/TUI и MAUI Desktop.
- [ ] Startup status и guided next actions приняты для всех lifecycle-состояний `implementation-critical`.
- [ ] English и Russian localization приняты для CLI/TUI и MAUI; fallback и extensibility подтверждены тестами.
- [ ] User/project config scopes, language ownership и раздельные artifact audiences приняты end-to-end.
- [ ] MVP release опубликован и успешно устанавливается глобально в Windows.

---

## Отложенный backlog — не входит в MVP

- Linux installer и `LinuxUpdateStrategy`.
- macOS installer и `MacOSUpdateStrategy`.
- .NET MAUI surface для macOS.
- Package-manager distribution для Windows/Linux/macOS.
- Machine-wide и enterprise installation.
- Workflow `standard`.
- Workflow `fast`.
- Distributed workers и централизованный scheduler.
- Multi-tenant/SaaS runtime.
- Enterprise policy integration.
- Расширенная release transparency и key rotation.

---

## Журнал evidence

| Task/Gate | Evidence: commit, PR, test run, artifact | Дата |
|---|---|---|
| P0.1–P0.30 | `docs/architecture/decisions/0001-stage-0-foundation.md`, `docs/contracts/v1/` | 2026-07-27 |
| Gate этапа 0 | `powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\contracts\Stage0.Contracts.Tests.ps1` — passed | 2026-07-27 |

## Открытые решения

| ID | Вопрос | Владелец | Срок | Статус |
|---|---|---|---|---|
| D-001 | Определить официальный GitHub repository Forge | Forge maintainers | 2026-07-27 | accepted in ADR 0001 |
| D-002 | Выбрать release signature/provenance mechanism | Forge maintainers | 2026-07-27 | accepted in ADR 0001 |
| D-003 | Утвердить Windows installation layout и atomic `current` implementation | Forge maintainers | 2026-07-27 | accepted in ADR 0001 |
| D-004 | Утвердить библиотеки CLI/TUI, MAUI UI/navigation, YAML и JSON Schema | Forge maintainers | 2026-07-27 | accepted in ADR 0001 |
| D-005 | Определить официальные install/update strategies Codex и Claude на Windows |  | Stage 5 | open; verify immediately before provider implementation |
| D-006 | Утвердить формат capability matrix и правила parity acceptance | Forge maintainers | 2026-07-27 | accepted in `capabilities.json` |
| D-007 | Утвердить Desktop process-lifetime/multi-instance model, recent projects и восстановление navigation intent | Forge maintainers | 2026-07-27 | accepted in ADR 0001 |
| D-008 | Утвердить ranking policy, UX suggested actions и правила выбора active sprint | Forge maintainers | 2026-07-27 | accepted in `recommendations.json` |
| D-009 | Выбрать localization catalog/formatting library и формат language-pack manifest | Forge maintainers | 2026-07-27 | accepted in ADR 0001 and schema |
| D-010 | Утвердить scoped-config schema registry, Windows user-config path и migration policy | Forge maintainers | 2026-07-27 | accepted in `configuration.json` |
| D-011 | Утвердить artifact audience registry и taxonomy user-facing/agent-facing/machine | Forge maintainers | 2026-07-27 | accepted in ADR 0001 and schemas |
