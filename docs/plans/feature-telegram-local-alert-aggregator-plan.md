# Plan: Telegram Local Alert Aggregator (Skywatch MVP)

| | |
|---|---|
| Spec | [docs/spec.md](../spec.md) — acceptance criteria referenced as `AC-Fx.y` |
| Source idea | [docs/source/telegram_local_alert_aggregator_feature_plan.md](../source/telegram_local_alert_aggregator_feature_plan.md) |
| Created | 2026-10-26 |
| Revised | 2026-10-27 — architect review applied (see §10) |
| Revised | 2026-10-30 — P8 added after the security and Telegram-policy review (owner request) |
| Revised | 2026-11-01 — AC-F1.9 fix: `SessionKey` must be hex of 32/48/64 characters; the first local `login` failed with a base64 key; AC-F1.3 fix: the run mode supplies `PhoneNumber` on resume, the first local run treated a valid session as rejected (owner approved) |
| Revised | 2026-11-01 — AC-F8.2 format: the disclaimer moves under the original text; the tag line is `🏷 КП` instead of pattern names and ends with `⚠️ Увага` so push previews still warn (owner request) |
| Revised | 2026-11-01 — AC-F8.14 added: mentions are shown in bold via message entities (owner request) |
| Revised | 2026-11-01 — P9 added: follow-ups, replies to notified posts (F13, D15; owner request after the first live night) |
| Revised | 2026-11-01 — AC-F10.10 added: `Notifications:LabelingOpenToAll` lets every member of the target chat label, on by default (owner request) |
| Revised | 2026-11-01 — AC-F10.11 added: `Notifications:LabelingEnabled` turns the label buttons on and off (owner request) |
| Phases | P0, P1a, P1b, P2, P3a, P3b, P4a, P4b, P5, P6a, P6b, P7, P8, P9 (14 phases, one Claude session each) |

---

## 1. Feature summary

**Name:** Telegram Local Alert Aggregator (codename *Skywatch*).

**Brief:** A .NET 10 Worker that listens to allowlisted Telegram channels and groups over MTProto, detects mentions of Krasnopavlivka in Ukrainian and Russian, removes duplicates, and posts unique matches with label buttons to a private test group. It is an experiment to measure precision, noise and latency.

**Full description:** Residents follow many Telegram channels that post real-time air-threat reports for the Kharkiv region. Mentions of their village are buried in high-volume streams and repeated across channels. Skywatch reads those channels with a dedicated user account (WTelegramClient), normalizes the text, and matches named, configurable regex patterns: full name in all UA/RU case forms, adjective forms, `КП` after a preposition, and the colloquial `Краснопе`. It persists metadata (and optionally text) in SQLite, deduplicates by forward origin and content hash, and sends unique, fresh (≤ 120 s) matches through a bot to a private test group. A labeler marks each notification useful/false/duplicate with inline buttons. A daily report computes volumes, precision per pattern, latency percentiles and connection stability. The MVP runs 3–7 days on a Linux VPS; any wider use is a separate decision. Hard safety constraints C1–C7 in the spec apply throughout: no trajectory inference, test group only, disclaimer on every message.

## 2. Goals and success criteria

| # | Criterion | Traces to |
|---|---|---|
| G1 | New messages from allowlisted sources are received in real time without importing history, and survive reconnects and restarts. | F1, F2, F3 |
| G2 | All agreed word forms match their pattern and all agreed negatives do not, proven by a test per form. | F4, F5, D10 |
| G3 | Reprocessing or reposting a message never produces a second notification within the duplicate window. | F6 |
| G4 | No notification is sent for a message older than 120 s; stale skips are visible in the group as one summary. | F7, D2 |
| G5 | Unique fresh matches appear in the test group in the agreed format, delivered at least once despite 429s, transient errors and restarts. | F8, D11 |
| G6 | Edits that introduce a mention are caught, according to the configured mode. | F9, D3 |
| G7 | Every match notification can be labeled by an authorized user in one tap. | F10, D5 |
| G8 | A daily report gives the numbers needed to evaluate success metrics 1–8 without manual SQL. | F11, spec §10 |
| G9 | The service runs unattended on a Linux VPS in Docker with persistent session and database, and restarts on failure. | N4, N7, D6 |
| G10 | No secret or identifying value ever reaches git; `/verify` (including gitleaks) is green on every phase. | C4, C5, D8 |

## 3. Architecture integration

The repository is greenfield (`README.md`, `LICENSE`, `.gitignore` only). There is no existing code to integrate with; everything below is new. The structure follows decisions D7, D12 and D13.

```text
koleso-skywatch/
├─ Skywatch.slnx · global.json · Directory.Build.props · Directory.Packages.props · .editorconfig · .gitleaks.toml
├─ Dockerfile · .dockerignore
├─ src/
│  ├─ Skywatch.Core/            pure logic, no IO packages (C7)
│  │  ├─ Normalization/         TextNormalizer, NormalizedText
│  │  ├─ Filtering/             KeywordFilter, PatternDefinition, FilterResult, PatternHit
│  │  ├─ Deduplication/         ContentHasher, OriginKey
│  │  ├─ Formatting/            NotificationFormatter, SourceLinkBuilder, DailyReportFormatter
│  │  └─ Time/                  StalenessPolicy, ReportPeriod, ClockSkewEstimator
│  └─ Skywatch.Worker/
│     ├─ Program.cs             host + run-mode dispatch (login | list-chats | rescan | run)
│     ├─ Options/               TelegramOptions, MonitoringOptions, NotificationOptions, ReportingOptions, StorageOptions + validators, TimeZoneResolver
│     ├─ Telegram/              ITelegramUpdateSource (+ SourceEvent, ResolvedSource records), WTelegramUpdateSource, TlMapper,
│     │                         WTelegramLogBridge, LoginCommand, ListChatsCommand,
│     │                         INotificationSender, SendResult, BotNotificationSender, BotCallbackListener
│     ├─ Persistence/           SkywatchDbContext, Entities/, Migrations/, UtcDateTimeConverter
│     ├─ Pipeline/              MessageProcessor, DuplicateDetector, EditHandler, LabelService, DailyReportBuilder, RescanCommand
│     └─ Hosting/               ProcessingService, NotificationDispatchService,
│                               StaleSummaryService, EditReplyService, DailyReportService, RetentionService
├─ tests/
│  ├─ Skywatch.Core.Tests/
│  └─ Skywatch.Worker.Tests/    (Fakes/: FakeUpdateSource, FakeNotificationSender; SqliteTestDb)
├─ build/verify.ps1 · build/install-hooks.ps1
├─ .claude/skills/verify/SKILL.md
├─ .github/workflows/ci.yml
├─ deploy/                      docker-compose.yml, .env.example, README (runbook)
└─ docs/                        spec.md, plans/, source/
```

**Runtime flow**

```text
WTelegramClient UpdateManager
   └─► WTelegramUpdateSource (allowlist, TlMapper → SourceEvent; owns Channel<SourceEvent>(1000, Wait))
          └─► ProcessingService (single hosted consumer: ResolveSourcesAsync → read loop → periodic TrySaveStateAsync)
                 └─► MessageProcessor
                        normalize → filter → persist (idempotent) → dedup → staleness → Notification(Pending)   [one transaction]
                                                                                   └─► wake-up signal (capacity-1 channel)
                                                                                          └─► NotificationDispatchService (reads Pending from DB) → INotificationSender (send gate, Telegram.Bot)
BotCallbackListener (long polling) ─► LabelService
StaleSummaryService (30 s) · EditReplyService (debounce → EditReply outbox row) · DailyReportService (09:00 Kyiv) · RetentionService (hourly, batched)
```

**Key design choices**

- **Outbox.** A `Pending` row is written in the same transaction as the message. The dispatch channel (capacity 1) is only a wake-up signal: on each signal and at startup the dispatcher reads `Pending` rows `ORDER BY Id` from the database, re-reads the status before each send and skips rows no longer `Pending` (AC-F8.11). Edit replies use the same outbox (`Kind = EditReply`).
- **Two Telegram seams only:** `ITelegramUpdateSource` and `INotificationSender` (D13). TL-type mapping lives in static `TlMapper` functions, so it can be unit-tested with hand-built TL objects.
- **Many writers, short transactions:** processing, dispatch, labeling and retention each use a new context from `IDbContextFactory<SkywatchDbContext>` per unit of work (one message, one send attempt, one retention batch, one button press); contention is handled by WAL + busy timeout. A single consumer of the update channel keeps message ordering.
- **Send gate:** `BotNotificationSender` serializes all bot sends, shares a 429 `blockedUntil` across senders (waits via `TimeProvider`), and sends summaries and reports only when no alert or edit reply is waiting.
- **Options pattern** with `ValidateOnStart`, validated per run mode (P1a). Regex patterns are compiled once into a singleton `KeywordFilter`.
- **No** MediatR, Polly, repository layer, AutoMapper or Serilog (spec §9).

## 4. Phases of work

Each phase ends with `/verify` green and a commit. Tests are named after the AC they prove (e.g. `AC_F5_3_abbreviation_after_preposition_matches`).

---

### P0 — Skeleton and gate chain

**Scope**
- `global.json` (SDK 10.0.401, `rollForward: latestFeature`, `"test": { "runner": "Microsoft.Testing.Platform" }`), `Directory.Build.props` (net10.0, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`), `Directory.Packages.props` (CPM), `.editorconfig`.
- `Skywatch.slnx` with the four projects. `Skywatch.Worker` is a minimal Worker host that starts, logs "Skywatch started" and stops cleanly. Project references: Worker → Core, test projects → their targets.
- One placeholder test per test project (xUnit v3 on Microsoft.Testing.Platform + Shouldly), so the test stage runs. Verify the exact xUnit v3 package set for MTP against current xUnit docs and record it in §5.
- `build/verify.ps1`: `dotnet restore` → `dotnet build -c Release` → `dotnet test -c Release --no-build` → `dotnet format --verify-no-changes` → gitleaks over **staged changes and history** (choose the exact commands for the installed gitleaks version; syntax changed from `protect --staged` to `git --staged`). Stops at the first failing stage with a non-zero exit code.
- `.gitleaks.toml`: custom rule for `-100\d{10}` channel ids in tracked files; obviously fake test ids (e.g. `-1000000000001`) allowlisted.
- Git pre-commit hook running gitleaks on staged changes; `build/install-hooks.ps1` installs it.
- `.claude/skills/verify/SKILL.md` wrapping the script; fill the Verification section of `CLAUDE.md`.
- `.github/workflows/ci.yml`: push/PR to `develop` on `ubuntu-latest`, checkout with `fetch-depth: 0`, setup-dotnet from `global.json`, same stages, gitleaks via CLI.
- `.gitignore` additions: `data/`, `*.db*`, `*.session`, `*.state`, `appsettings.*.local.json`, `appsettings.Development.json`, `appsettings.Production.json`.
- `Skywatch.Worker` gets `UserSecretsId`.

**Out of scope:** any Telegram, EF or domain code.

**Acceptance criteria**
1. `pwsh build/verify.ps1` passes on a clean clone on Windows.
2. The CI workflow passes on GitHub for the P0 commit.
3. A committed fake token pattern (tested locally on a throwaway branch, never pushed) makes gitleaks fail `/verify`; a staged (uncommitted) one fails it too, and the pre-commit hook blocks the commit.
4. `dotnet run --project src/Skywatch.Worker` starts and stops on Ctrl+C with exit code 0.
5. `Skywatch.Core.csproj` has no package references.
6. A real-looking `-100` + 10-digit chat id in a tracked file fails gitleaks; the allowlisted fake ids pass.

**Tests-first list**
- Placeholder test in each test project (proves the test stage runs).
- Committed `appsettings.json` has empty `Sources`, `TargetChatId`, `LabelerUserIds`.
- (Manual) gitleaks negative checks, from criteria 3 and 6.

**Files:** all of the above. The contract exposed is the `/verify` entry point.

**Manual steps for the owner:** enable GitHub Secret Scanning + Push Protection; `winget install gitleaks`; run `build/install-hooks.ps1`.

---

### P1a — Login and configuration

**Scope**
- `TelegramOptions`, `MonitoringOptions.Sources` with validation per run mode (table below). User Secrets wiring. `appsettings.json` with empty identifying values, plus `appsettings.Example.json`.
- Run-mode dispatch in `Program.cs`: `login`, `list-chats`, default run (`rescan` is added in P6b).
- `LoginCommand` (AC-F1.1, F1.4), `ListChatsCommand` (AC-F1.2), session-missing behaviour (AC-F1.3).
- `TlMapper`: marked chat id ↔ peer id conversion (needed by `list-chats`; P1b extends `TlMapper`).
- `WTelegramLogBridge`: WTelegramClient's own logging routed into `ILogger` at `Warning`+, known secret values replaced with `***` (F1.5).

| Mode | Validated | Database |
|---|---|---|
| `login` | `ApiId`, `ApiHash`, `SessionKey` (session file may not exist yet) | not touched |
| `list-chats` | same + session file exists | not touched |
| `rescan` (P6b) | `Monitoring:Patterns`, `Storage` | read-only, no migrations |
| `run` | everything (spec §6) | migrations + WAL (from P3a) |

**Out of scope:** the listener, normalization, filtering, persistence, the bot.

**Acceptance criteria**
1. AC-F1.1–F1.4, AC-F1.5 (filter test and manual login check; the run-mode start test lands in P1b), AC-F2.1 hold.
2. Manual: `list-chats` output is enough to fill `Monitoring:Sources` without other tools.
3. Manual: run `login`, search the log for the phone number and the code → none found.

**Tests-first list**
- Marked id conversion: channel `-100xxxxxxxxxx` ↔ channel id; basic group `-xxxx` ↔ chat id; round trip.
- Options validation: missing `ApiId`/`ApiHash`/`SessionKey` in run mode fails startup; `login` does not require the session file to already exist on disk; `list-chats` does.
- Run without a session file → exit code ≠ 0 and a `Critical` log containing "login" (F1.3).
- `WTelegramLogBridge`: known secret values in input lines are replaced with `***` (F1.5).

**Files:** `Program.cs`; `Options/TelegramOptions.cs`, `Options/MonitoringOptions.cs` (+ validators); `Telegram/TlMapper.cs` (id conversion), `LoginCommand.cs`, `ListChatsCommand.cs`, `WTelegramLogBridge.cs`; `appsettings.json`, `appsettings.Example.json`; tests.
**Contracts:** run modes `login | list-chats | run`; per-mode options validation (table above).

---

### P1b — Listener

**Scope**
- The full seam contract (records live in `Worker/Telegram/` next to the interface; no TL types in them):

  ```csharp
  interface ITelegramUpdateSource
  {
      Task<IReadOnlyList<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct);
      IAsyncEnumerable<SourceEvent> ReadAllAsync(CancellationToken ct);
      Task<bool> TrySaveStateAsync(CancellationToken ct);
  }

  abstract record SourceEvent;
  record IncomingMessage(...) : SourceEvent;                       // spec F3
  record ConnectionStateChanged(ConnectionState State, DateTime AtUtc) : SourceEvent;

  record ResolvedSource(long ChatId, string Name, SourceType Type,
                        string? Username, bool NoForwards, bool Resolved);
  ```
- `OriginKey`. `WTelegramUpdateSource` using `UpdateManager` with state file `Telegram:UpdateStatePath`. It owns the bounded channel (F3.4): the update handler writes to it, `ReadAllAsync` reads from it. Connection events flow through the same channel as messages (order kept, single DB writer). Allowlist filter (F2.5).
- Source resolution at startup via `ResolveSourcesAsync` (AC-F2.2 without DB upsert, F2.3, F2.4); P3a adds the upsert. Then `ReadAllAsync`.
- `TlMapper` static functions: text/caption extraction (F3.5), forward origin (F3.6), edit mapping (F3.9).
- Update-state save policy: `TrySaveStateAsync` saves only when the channel is empty, otherwise returns `false`. A single hosted `ProcessingService`: `ResolveSourcesAsync` → read loop → after each event, if `Telegram:UpdateStateSaveIntervalSeconds` (default 60) elapsed, `TrySaveStateAsync` (single consumer, so nothing is in flight) → on shutdown stop reading, drain the channel through processing, then `TrySaveStateAsync`. After a crash, replay from the last saved state is possible and intended; F6.2 (idempotent insert) and staleness make it safe.
- Temporary `ProcessingService` body that logs each `IncomingMessage` safely: source name, message id, text length (not text), and `ReceivedAt − PublishedAt` latency.
- Passive clock-skew check (F3.8): hourly, pure Core `ClockSkewEstimator.Evaluate(IEnumerable<(DateTime publishedUtc, DateTime receivedUtc)>)` over live messages of the last hour (exclude differences > 120 s, i.e. catch-up); `Warning` if the minimum > 10 s or any value < −2 s; no data → `Debug` and skip. No `Help_GetConfig` call.
- Disconnect/reconnect logging (F3.7, log only) from `ConnectionStateChanged`.
- Channel back-pressure: full mode stays wait; `Warning` with the current size when the channel is > 80% full, at most once per minute.

**Out of scope:** normalization, filtering, persistence, the bot.

**Acceptance criteria**
1. AC-F1.5 (run-mode start test), AC-F2.3–F2.5, AC-F3.1–F3.6, F3.8, F3.9 hold.
2. Manual: with the personal account and 2–3 test channels, a new post appears in the log within seconds; restarting does not print old posts from before the first start; a post published while the service was stopped is printed after restart, with its latency.

**Tests-first list**
- `TlMapper`: text message → `IncomingMessage` with correct ids and UTC times; media with caption → caption used; media without caption → null (discarded); forwarded channel post → origin key set; non-forward → origin = self; `UpdateEditChannelMessage` → `IsEdit` + `EditedAtUtc`.
- Allowlist: an update from a non-allowlisted chat is not written to the channel (F2.5).
- State save: no save while the channel is non-empty; save when it is empty; on shutdown the channel is drained, then state is saved.
- `ClockSkewEstimator`: `[Theory]` rows over latency sets (normal, minimum > 10 s, a value < −2 s, catch-up excluded, no data).
- Back-pressure: fill to 801 → one `Warning`.
- Host start in run mode with known fake secrets in config and `FakeUpdateSource`: captured logs contain no secret (F1.5). Tests touching the static `Helpers.Log` are non-parallel.
- `FakeUpdateSource` can emit messages, connection events, a configured `ResolvedSource` list, and records `TrySaveStateAsync` calls.

**Files:** `Telegram/ITelegramUpdateSource.cs` (+ contract records), `WTelegramUpdateSource.cs`, `TlMapper.cs`; `Hosting/ProcessingService.cs` (temporary logger body); `Core/Deduplication/OriginKey.cs`; `Core/Time/ClockSkewEstimator.cs`; `Options/TelegramOptions.cs` (`UpdateStateSaveIntervalSeconds`); tests + `Fakes/FakeUpdateSource.cs`.
**Contracts:** `ITelegramUpdateSource` and the records above.

---

### P2 — Normalizer and filter

**Scope**
- `TextNormalizer` → `NormalizedText(Lower, CasePreserved)` (AC-F4.1–F4.7).
- `PatternDefinition`, `KeywordFilter`, `FilterResult`, `PatternHit` (AC-F5.1–F5.11), with the default patterns from spec F5 in `appsettings.json`.
- Options validation for patterns (F5.9).
- `ProcessingService` now logs matches: source, pattern names, latency. Still no text in logs.

**Out of scope:** persistence, hashing, notifications.

**Acceptance criteria:** AC-F4.1–F4.7 and AC-F5.1–F5.11.

**Tests-first list**
- `[Theory]` per AC-F5.1 form (UA and RU, all cases, upper/mixed case, hyphenated, hashtag).
- `[Theory]` AC-F5.2 adjective positives, and "does not hit `Settlement`".
- `[Theory]` AC-F5.3 abbreviation positives; AC-F5.4 negatives, including lower-case `на кп`.
- `[Theory]` AC-F5.5 colloquial positives; negatives `Красноперекопськ`, `красноперка`, `краснопёрка`.
- AC-F5.6 inside-word negative; AC-F5.7 multiple hits in order; AC-F5.8 disabled pattern skipped; AC-F5.11 empty text.
- AC-F5.9: invalid regex / duplicate name / empty name / no enabled patterns → validation failure.
- AC-F5.10: a catastrophic-backtracking pattern with a long input → no throw, `Warning` logged, other patterns still evaluated.
- Normalizer: NFKC (full-width letters), each zero-width char and soft hyphen inside `Краснопавлівка` still matches after normalization; whitespace/NBSP/newlines collapse; `ё→е`; `І/Ї` lowercase; `і`≠`и`; Latin `i` in `Краснопавлiвка` is NOT converted (documents D10); null/whitespace input.
- A real-world sample set in `tests/Skywatch.Core.Tests/Samples/` (anonymized alert texts written for tests, not copied from channels), with expected pattern names. Shared by Core tests and the Worker test below.
- Worker test: loads the project's `appsettings.json` the same way the host does, binds `Monitoring:Patterns`, runs the F5.1 and F5.3 positive and negative samples (guards against JSON escaping errors, `"\b"` = backspace).

**Files:** `Core/Normalization/*`, `Core/Filtering/*`, `Worker/Options/MonitoringOptions.cs` (patterns + validator), `Worker/Hosting/ProcessingService.cs`, `appsettings.json`, Core tests, Worker patterns-from-config test.
**Contracts:** `TextNormalizer.Normalize(string?) : NormalizedText`; `KeywordFilter.Match(NormalizedText) : FilterResult`.

---

### P3a — Storage and retention

**Scope**
- EF Core SQLite: `SkywatchDbContext`, entities `Source`, `Message`, `Notification`, `ServiceEvent`, `UtcDateTimeConverter`, indexes. The initial migration creates the **full** spec §7 schema, including `Notifications.Kind`, label columns, all statuses and `Messages.DuplicateReason`. Foreign keys at DB level (because `ExecuteDelete` bypasses the change tracker): `Notifications.MessageId` ON DELETE CASCADE; `Messages.DuplicateOfMessageId` ON DELETE SET NULL (`IsDuplicate` stays true). Migrate at startup, then `PRAGMA journal_mode=WAL` once. `dotnet-ef` as a local tool (`.config/dotnet-tools.json`).
- `StorageOptions`, including `BusyTimeoutSeconds` (default 30, connection string `Default Timeout`) and `RetentionBatchSize` (default 500). `IDbContextFactory<SkywatchDbContext>`; a new context per unit of work; never inject `DbContext` into singleton services.
- Source upsert at startup from `ResolveSourcesAsync` (completes AC-F2.2).
- `ServiceEvents` for Started/Stopped/Disconnected/Reconnected (completes F3.7).
- `RetentionService` (F12.1–F12.4), deleting in batches of `RetentionBatchSize`, each in its own short transaction.

**Out of scope:** hashing, duplicate detection, the message pipeline, sending, edits.

**Acceptance criteria:** AC-F2.2, F3.7, F12.1–F12.4.

**Tests-first list** (Worker.Tests, SQLite in-memory, `FakeTimeProvider`)
- `UtcDateTimeConverter`: value read back has `Kind=Utc`; range queries on `ReceivedAtUtc` are translated to SQL (no client evaluation exception).
- Source upsert: new, changed name, disabled in config.
- `ConnectionStateChanged` events → `Disconnected` / `Reconnected` service events.
- Retention at boundaries for each of the three rules; service events are deleted too.
- Retention boundary: original older than 30 days, its duplicate younger → original and its notification deleted, duplicate kept with null reference, no error.
- Smoke test on a temp file DB: batched retention concurrent with inserts → no `SqliteException`.

**Files:** `Worker/Persistence/**`, `Worker/Options/StorageOptions.cs`, `Worker/Hosting/RetentionService.cs`, `ProcessingService.cs` (upsert, connection events); tests.
**Contracts:** `IDbContextFactory<SkywatchDbContext>`; schema = spec §7.

---

### P3b — Matching pipeline and deduplication

**Scope**
- `ContentHasher` (F6.3). `DuplicateDetector` (F6.4): origin-key (forward) duplicates within `Monitoring:DuplicateWindowMinutes` (60); identical text within `Monitoring:ContentDuplicateWindowMinutes` (10), and only from a **different** source when `Monitoring:ContentDuplicateAcrossSourcesOnly = true` (a repeat in the same source is always sent). Stores `DuplicateReason` (`Forward` / `SameText`).
- `MessageProcessor`: idempotent insert (F6.2), text-storage rule (F6.1), duplicate marking (F6.5), `Pending` notification in the same transaction (F6.6) or log-only when disabled (F6.7), error isolation with a `ProcessingError` service event (F6.8). Staleness check at creation via `StalenessPolicy` (F7.1 creation-time part only, status `SkippedStale`).
- `NotificationOptions` with `Enabled` and `MaxMessageAgeSeconds` and a validator (`MaxMessageAgeSeconds > 0`). With `Enabled = true`, `Pending` rows are created but nothing sends them until P4b, which picks them up via startup recovery (AC-F8.11). This is intended.
- `ProcessingService` delegates to `MessageProcessor`. New-message events only; edit events are logged and ignored until P5.

**Out of scope:** sending anything; edits.

**Acceptance criteria:** AC-F6.1–F6.8, F7.1 (creation time), F7.2. Manual: after a day of local running, `data/skywatch.db` holds metadata rows for all received messages and text only for matches when `StoreUnmatchedText=false`.

**Tests-first list** (Worker.Tests, SQLite in-memory, `FakeTimeProvider`)
- Same `(source, msgId)` processed twice → one row, one notification (F6.2), including a concurrent double-insert surfacing as a unique-constraint violation that is handled as "already processed"; after the violation the next message is processed (fresh context).
- Unmatched + `StoreUnmatchedText=true` → text stored; `=false` → text null, metadata present.
- Hash: known input → known SHA-256 hex; two texts differing only by zero-width chars or whitespace → same hash.
- Duplicate by origin key: original then forward within 60 min → forward `IsDuplicate`, reason `Forward`, `SkippedDuplicate`. Forward first, then original → original is the duplicate.
- Same text from a different source within 10 min → duplicate, reason `SameText`; same pair after 10 min → both unique; same text repeated in the same source → not a duplicate with `ContentDuplicateAcrossSourcesOnly=true`, duplicate with `=false`.
- A duplicate of a duplicate points to the first non-duplicate.
- Notifications disabled → no notification rows; match logged.
- Stale at creation (age 121 s) → `SkippedStale`; age 120 s → `Pending`.
- Exception in processing one message → `ProcessingError` event and the next message is processed.

**Files:** `Worker/Pipeline/MessageProcessor.cs`, `DuplicateDetector.cs`, `Core/Deduplication/ContentHasher.cs`, `Core/Time/StalenessPolicy.cs`, `Worker/Options/NotificationOptions.cs`, `Worker/Options/MonitoringOptions.cs` (dedup windows), `ProcessingService.cs`; tests.
**Contracts:** `MessageProcessor.ProcessAsync(IncomingMessage, CancellationToken) : Task<ProcessOutcome>`; `StalenessPolicy.IsStale(DateTime eventTimeUtc, DateTime nowUtc)`.

---

### P4a — Notification sender and formatting

**Scope**
- `NotificationOptions` extended: `BotToken`, `TargetChatId`, other send settings, and the conditional validation from spec §6 (`Enabled = true` requires `BotToken` and `TargetChatId`).
- `INotificationSender`, `SendResult`, `BotNotificationSender` (Telegram.Bot, plain text, no parse mode). Send gate: serializes all bot sends; on 429 stores `blockedUntil` and every sender waits until then (via `TimeProvider`); alerts and edit replies have priority, so summaries and reports are sent only when no alert is waiting.
- `NotificationFormatter` (F8.2, F8.4, F8.5, F8.12), `SourceLinkBuilder` (F8.3), pure Core. Truncation does not split surrogate pairs: if the last kept char is a high surrogate, step back one char (splitting a multi-codepoint flag is accepted).
- `TimeZoneResolver` (Worker): try `Europe/Kyiv`, fall back to `Europe/Kiev`; if neither resolves, fail startup with a clear error. Log the resolved zone and current Kyiv time. Core receives a `TimeZoneInfo` (C7).

**Out of scope:** dispatching, staleness recheck, the stale summary, edits, labeling, daily report.

**Acceptance criteria:** AC-F7.3 (formatter/age part), F8.2–F8.5, F8.12, F8.13.

**Tests-first list**
- Formatter: exact output for a public channel, a private channel, and a basic group (no link line); pattern tags; edit marker; Kyiv time across the 25 Oct 2026 DST change (summer vs winter time); latency rounding; 5000-char text truncated with `…` and total ≤ 4096; header/footer intact; `[Theory]` row: emoji exactly at the cut point → valid string, length ≤ 4096, ends with `…`.
- Link builder: username link; `t.me/c/{id}` from a `-100…` marked id; basic group → null.
- `TimeZoneResolver` with a fake lookup: `Kyiv` missing / `Kiev` present → `Kiev`; both missing → error.
- Send gate (`FakeTimeProvider`): after a 429 with `retry_after`, a summary send waits until then; an alert and a report ready at the same time → the alert is sent first.
- Options: `Enabled = true` without `BotToken` or `TargetChatId` fails startup.

**Files:** `Worker/Telegram/INotificationSender.cs`, `SendResult.cs`, `BotNotificationSender.cs`; `Core/Formatting/NotificationFormatter.cs`, `SourceLinkBuilder.cs`; `Worker/Options/NotificationOptions.cs` (extend), `TimeZoneResolver.cs`; tests + `Fakes/FakeNotificationSender.cs`.
**Contracts:** `INotificationSender.SendAsync(OutgoingNotification, CancellationToken) : Task<SendResult>`, `ReplyAsync(long replyToBotMessageId, OutgoingNotification, CancellationToken)`; each send carries its priority (alert / edit reply vs summary / report); `SendResult` = Success(botMessageId) | RateLimited(retryAfter) | Transient(error, deliveryUnknown) | Fatal(error).

---

### P4b — Notification dispatcher

**Scope**
- `NotificationDispatchService`: single consumer woken by a capacity-1 signal channel; on signal and at startup reads `Pending ORDER BY Id` from the DB, re-reads the status before each send and skips rows no longer `Pending`. Startup recovery of `Pending` (F8.1, F8.11), staleness recheck before each attempt (F7.1), 429 handling (F8.6), retry with backoff (F8.7), fatal 4xx (F8.8), `PossibleDuplicate` (F8.9), success bookkeeping (F8.10).
- `StaleSummaryService` (F7.4): not an outbox row; send first, set `StaleReportedAtUtc` only on success; on failure retry on the next 30 s tick.
- `MessageProcessor` signals the dispatcher after commit.
- Buttons are NOT attached yet (P6a).

**Out of scope:** edits, labeling, daily report.

**Acceptance criteria:** AC-F7.1 (all three check points), F7.4, F8.1, F8.6–F8.11. Manual: with notifications enabled, a test post containing `на КП` in a test channel appears in the test group within ~5 s in the agreed format; a post forwarded to a second test channel does not produce a second message.

**Tests-first list**
- Dispatcher with `FakeNotificationSender` + `FakeTimeProvider`:
  - Success → `Sent`, `BotMessageId`, `SentAtUtc`.
  - 429 with `retry_after=3` → waits 3 s, then sends; attempts not incremented.
  - Transient ×2 then success → `Sent`, `Attempts=3`, delays 1 s, 2 s.
  - Transient ×3 → `Failed` with error.
  - 400/403 → `Failed` immediately + `Critical`.
  - Timeout with unknown outcome → retried, `PossibleDuplicate=true`.
  - Message becomes stale between attempts → `SkippedStale`, no further send.
  - Startup with 3 `Pending` rows (one stale) → 2 sent in creation order, 1 `SkippedStale`.
  - Ordering: notifications created A, B, C → sent A, B, C.
  - A row whose status changed after it was read → skipped.
- Stale summary: 3 unreported stale → one summary with N=3 and time range, all marked; nothing unreported → nothing sent; send failure → nothing marked, retried on the next tick.
- End-to-end pipeline test: `FakeUpdateSource` → processor → dispatcher → `FakeNotificationSender`, covering match, duplicate, stale, non-match.

**Files:** `Worker/Hosting/NotificationDispatchService.cs`, `StaleSummaryService.cs`; `MessageProcessor.cs` (signal); tests.
**Contracts:** dispatcher wake-up signal (capacity-1 channel); no notification ids in the channel.

---

### P5 — Edited messages

**Scope**
- `EditHandler` with the three `EditHandling` modes (F9.1–F9.6). Uses existing normalize/filter/dedup/staleness (edit time) and outbox.
- Uses `Notification.Kind` = `EditNewMatch` / `EditReply` from the P3a schema. No migration in this phase (a needed one is a deviation).
- `EditReplyService` only debounces per message (`EditDebounceSeconds`), then creates `Notification(Kind=EditReply, Pending)` and signals the dispatcher. The dispatcher sends it through `INotificationSender.ReplyAsync` to the original `BotMessageId`, with the same retry / 429 / `PossibleDuplicate` / staleness recheck (age from `EditedAtUtc`, F7.3).
- Remove the P3b "edits ignored" stub from `ProcessingService`.

**Out of scope:** labeling (edit replies get no buttons).

**Acceptance criteria:** AC-F9.1–F9.6, AC-F7.3. Manual: editing a test-channel post to add `Краснопавлівка` produces a `✏️ оновлено` notification within ~5 s.

**Tests-first list**
- `Ignore`: edit adding a mention → nothing changes.
- `NotifyOnNewMatch`: unmatched → matched edit → `EditNewMatch` notification with marker; matched → matched edit → none; unknown message edit that matches → notification; edit whose new text duplicates another recent match → `SkippedDuplicate`; edit with `EditedAt` 121 s ago → `SkippedStale`; edit with identical hash → ignored.
- `ReplyOnChange`: sent message edited 3 times within 10 s → exactly one `EditReply` notification 15 s after the last edit, sent by the dispatcher as a reply with the final text; `EditReply` older than 120 s from `EditedAtUtc` at send time → `SkippedStale`; edit to a not-yet-sent (`Pending`) message → no reply; edit when `BotMessageId` is null → no reply, `Warning`.
- Text-storage rule applies to edits (F9.2 with `StoreUnmatchedText=false`).

**Files:** `Worker/Pipeline/EditHandler.cs`, `Worker/Hosting/EditReplyService.cs`, `ProcessingService.cs`, `NotificationDispatchService.cs` (`EditReply` → `ReplyAsync`), `NotificationFormatter.cs` (reply variant); tests.
**Contracts:** `EditHandler.HandleAsync(IncomingMessage, CancellationToken)`.

---

### P6a — Labeling

**Scope**
- Inline keyboard on `New` / `EditNewMatch` notifications (F10.1).
- `BotCallbackListener` hosted service: long polling for `callback_query` only (F10.6). It must not conflict with the sending client (same bot token; there is no webhook).
- `LabelService` (F10.2–F10.5). A callback is accepted only when `callback.Message.Chat.Id == TargetChatId` **and** the user is in `LabelerUserIds`; otherwise answer `Немає прав` and log `Warning` with the chat id.

**Out of scope:** daily report, `/missed` command.

**Acceptance criteria:** AC-F10.1–F10.6. Manual: pressing buttons in the test group updates the database.

**Tests-first list**
- Callback-data parse: valid `lbl:42:u|f|d`; malformed; unknown id.
- Authorized press → label stored; buttons re-rendered with `✔`; a second press with a different label overwrites.
- Unauthorized press → nothing stored; answer `Немає прав`.
- Authorized user, foreign chat → no label stored.

**Files:** `Worker/Telegram/BotCallbackListener.cs`, `BotNotificationSender.cs` (keyboard, edit markup, answer callback); `Worker/Pipeline/LabelService.cs`; tests.
**Contracts:** `INotificationSender` gains `AnswerCallbackAsync`, `SetLabelMarkupAsync`; `LabelService.ApplyAsync(long notificationId, Label, long userId, CancellationToken) : Task<LabelOutcome>`.

---

### P6b — Daily report and rescan

**Scope**
- `ReportingOptions`, `ReportPeriod` (Kyiv day bounds with DST), `DailyReportBuilder` (queries), `DailyReportFormatter`, `DailyReportService` with catch-up and once-per-day guarantee via a `DailyReportSent` service event (F11.1–F11.5). The report is not an outbox row: send first, write `DailyReportSent` only on success, retry with backoff until success; it goes through the send gate at low priority.
- Duplicates are split in the report: forwards N / same text M. A labeled message counts toward **every** matched pattern; the report notes that one message can count in several patterns.
- `rescan` run mode (AC-F1.6): `dotnet run --project src/Skywatch.Worker -- rescan [--pattern "<regex>"]` reads unmatched messages with stored text, runs the current config patterns (or a one-off regex), prints date, source, message id, matching pattern and a text snippet to the console. Read-only: sends nothing, writes nothing, no migrations.

**Out of scope:** `/missed` command, charts, CSV export.

**Acceptance criteria:** AC-F1.6, AC-F11.1–F11.5. Manual: the 09:00 report appears with plausible numbers.

**Tests-first list**
- `ReportPeriod` for 2026-10-25 Kyiv → 25 hours; 2026-03-29 → 23 hours; a normal day → 24 hours; bounds in UTC.
- Report builder on seeded data: counts per pattern, duplicates split forwards / same text, statuses, labels, precision overall and per pattern (a message with two patterns counts in both), `n/a` with zero labels, p50/p95 on a known distribution, reconnect count and total disconnected time from events (including a disconnect still open at the period end).
- Scheduler with `FakeTimeProvider`: fires at 09:00 local; service down at 09:00 and started at 11:00 → sends once; restart after sending → does not resend; send failure → no `DailyReportSent`, retried with backoff.
- Notifications disabled → report goes to the log.
- `rescan`: stored unmatched text that matches a config pattern / a `--pattern` regex is printed; row counts unchanged afterwards; no migration applied.

**Files:** `Worker/Pipeline/DailyReportBuilder.cs`, `RescanCommand.cs`; `Core/Formatting/DailyReportFormatter.cs`; `Core/Time/ReportPeriod.cs`; `Worker/Hosting/DailyReportService.cs`; `Worker/Options/ReportingOptions.cs`; `Program.cs` (`rescan` mode); tests.
**Contracts:** run mode `rescan [--pattern "<regex>"]`.

---

### P7 — Deployment

**Scope**
- `Dockerfile` at root: multi-stage (`sdk:10.0` → `runtime:10.0` or `aspnet`-free `runtime-deps` if self-contained is chosen), non-root user, `/app/data` created and owned by the `app` user (the named volume inherits it), tzdata included (chiseled `-extra` variant or install `tzdata`). The `TZ` env var is not needed, the zone database is.
- `.dockerignore`; `deploy/docker-compose.yml` with `restart: unless-stopped` (Docker backs off up to 1 min), named volume for `data/`, `env_file: .env`; `deploy/.env.example` with every variable name and no values.
- JSON console logging in containers (N6), log rotation through Docker's `json-file` options.
- Graceful shutdown: `HostOptions.ShutdownTimeout = 30 s`, channel drain, `Stopped` event (N4).
- `UseWindowsService()` for the Windows path.
- `deploy/README.md` runbook: VPS provisioning (minimal), `login` with the dedicated account, session transfer (`session.dat` + same `SessionKey`), `list-chats`, filling `.env`, start/stop/logs, on `Critical … run login`: stop the container, run `login`, start again; DB backup/copy for analysis; "Reset the database": stop → delete `data/skywatch.db*` (`.db`, `-wal`, `-shm`) → start (migrations recreate it), do **not** delete the session file or the update state file; the `rescan` command for metric 6; Windows Service install (`sc.exe create`).
- Pre-run checklist: dedicated account warmed up, bot in group, `Notifications:Enabled=true`, `StoreUnmatchedText=true`, `Storage:UnmatchedRetentionDays = 8` (experiment length + 1; spec default stays 3), NTP enabled on the VPS.

**Out of scope:** CI deploy, IaC, monitoring stack.

**Acceptance criteria**
1. `docker build .` succeeds; the container runs as non-root.
2. `docker compose up -d` with a valid `.env` and a copied session runs the pipeline; `docker compose restart` keeps the session and DB, and `Pending` notifications survive.
3. `docker stop` produces a `Stopped` event and exits within 30 s.
4. Running `login` interactively in the container (`docker compose run --rm skywatch login`) works as an alternative to copying a session.
5. The runbook is followed end to end once on the actual VPS before the experiment starts.
6. `docker kill` during traffic → restart → nothing lost, nothing duplicated in the group.
7. The container log shows the correct Kyiv time.
8. Fresh volume → the container starts, DB and state file are created.

**Tests-first list**
- Graceful shutdown test: host with `FakeUpdateSource` holding queued messages, stop requested → all queued messages processed or still `Pending`, `Stopped` event written.
- Options binding from environment variables (`Notifications__BotToken` etc.) test.
- (Manual) criteria 1–8.

**Files:** `Dockerfile`, `.dockerignore`, `deploy/*`, `Program.cs` (Windows service, shutdown, JSON logging), tests.

### P8 — Hardening after review

Added 2026-10-30 at the owner's request after a security review and a Telegram-policy (ban risk) review of P0–P7. Owner decisions: bot pacing (≈20 messages/min per group) is not needed, match traffic is low; the Windows Service folder permissions are accepted, the owner is the machine's only admin.

**Scope**
- One process per session: `SessionFiles` lock on `{SessionPath}.lock` (`FileShare.None`, an advisory `flock` on Linux) taken in `Program.RunAsync` for `login`, `list-chats` and the run mode (AC-F1.7).
- Session rejection marker `{SessionPath}.rejected`: written by `ProcessingService` when the session is rejected at start or while reconnecting (new `SessionRejected` source event); checked by `TelegramOptionsValidator` for the run mode and `list-chats`; deleted by `login` (AC-F1.8). This stops Docker's `restart: unless-stopped` from reconnecting with a dead session in a loop.
- `TelegramRetry`: FLOOD_WAIT honoured at start and on reconnect (AC-F3.10); reconnect recreates the client and the `UpdateManager` from the current state, backoff 10 s → 10 min, 401 stops (AC-F3.11).
- Fixed device identity options and a `SessionKey` WTelegramClient can use: hex of 32, 48 or 64 characters (AC-F1.9; corrected 2026-11-01, the first version only required ≥ 16 characters and the docs suggested base64).
- Labeling: press tied to `BotMessageId` and `Sent` (AC-F10.7); answered on a storage failure (AC-F10.8); `getUpdates` 429 uses `retry_after`, a 429 on answering a press does not block the send gate (AC-F10.9).
- `WTelegramLogBridge` registered in DI; `ProcessingService` redacts listener start errors with it (AC-F1.5).
- `.dockerignore` excludes local `appsettings.Development/Production/*.local.json`; `deploy/README.md`: stop before `list-chats`/`login`, log in on the VPS, delete a copied session, `chmod 600`, `umask 077` for backups, `--pull`, the rejection marker; `.env.example`: device identity keys.

**Out of scope:** bot pacing; Windows folder ACLs; persisting a FLOOD_WAIT "not before" across restarts (the service waits in process instead).

**Acceptance criteria:** AC-F1.5, AC-F1.7–F1.9, AC-F3.10, AC-F3.11, AC-F10.7–F10.9. Manual: `docker compose run --rm skywatch list-chats` while the service runs fails with the lock message.

**Tests-first list**
- Second lock on the same session fails; run mode with a held lock exits non-zero with a Critical and never resolves sources.
- Rejection at start writes the marker; the next start fails validation with `login` in the Critical; `login` success clears it (marker helpers).
- Reconnect loop with a scripted reconnect step and `FakeTimeProvider`: FLOOD_WAIT X waits X + 5 s; failures back off 10, 20, 40 s capped at 10 min; rejection stops and emits `SessionRejected`.
- Validator: short `SessionKey` fails; device identity values reach the WTelegram config callback.
- Label press on another bot message or a non-`Sent` notification → `UnknownNotification`; storage failure → answered with the error text.
- Callback listener: 429 waits `retry_after`; sender: a 429 on `AnswerCallbackAsync` leaves the gate open.

**Files:** `Telegram/SessionFiles.cs`, `Telegram/TelegramRetry.cs`, `Telegram/WTelegramUpdateSource.cs`, `Telegram/ITelegramUpdateSource.cs`, `Telegram/LoginCommand.cs`, `Telegram/BotNotificationSender.cs`, `Telegram/BotCallbackListener.cs`, `Options/TelegramOptions.cs`, `Hosting/ProcessingService.cs`, `Pipeline/LabelService.cs`, `Program.cs`, `.dockerignore`, `deploy/*`, `docs/spec.md`, tests.

### P9 — Follow-ups (replies to notified posts)

Added 2026-11-01 at the owner's request after the first live night: a source posts "Реактивний БПЛА на Краснопавлівку", then replies to it "Не фіксується"; the channel showed only the first post. Owner decision (D15): relaying the source's own reply verbatim is within C1; no parsing of directions.

**Scope**
- `TlMapper` maps `reply_to` (same chat only) to `IncomingMessage.ReplyToMessageId`.
- `Message.ReplyToTelegramMessageId`, `Message.FollowUpOfMessageId` (FK to `Messages`, set null on delete); migration `AddFollowUps`.
- `MessageProcessor`: an unmatched reply to a message with a `Sent` alert, within the window and under the per-post limit, gets a `FollowUp` notification in the same transaction; the root of a reply to a follow-up is that follow-up's original (AC-F13.1, F13.2, F13.5).
- `NotificationDispatchService`: `FollowUp` goes out as a reply to the original alert, without label buttons; staleness from its own publication (AC-F13.3, F13.4). `NotificationFormatter`: follow-up first line.
- `MonitoringOptions`: `FollowUpsEnabled`, `FollowUpWindowMinutes`, `FollowUpMaxPerMessage` with validation (AC-F13.6).
- `DailyReportBuilder` leaves follow-ups out (AC-F13.7).

**Out of scope:** replies from another chat (comments, cross-channel replies); interpreting the reply; label buttons on follow-ups; follow-ups of edits that newly match.

**Acceptance criteria:** AC-F13.1–F13.7.

**Tests-first list**
- Mapper: a reply in the same chat carries the replied-to id; a reply to another peer does not.
- Processor: unmatched reply to a sent alert → `FollowUp` Pending with text stored; to an unsent / unknown / out-of-window message → plain unmatched; the sixth follow-up → no notification; a reply to a follow-up → same original; a matching reply → normal `New`; feature off → plain unmatched.
- Dispatcher: a follow-up is a reply to the original's bot message, no label buttons, follow-up first line; stale from its own time.
- Report: follow-ups are not counted.

**Files:** `Telegram/TlMapper.cs`, `Telegram/ITelegramUpdateSource.cs`, `Persistence/Entities/Message.cs`, `Persistence/Entities/Notification.cs`, `Persistence/SkywatchDbContext.cs`, `Persistence/Migrations/*`, `Pipeline/MessageProcessor.cs`, `Pipeline/DailyReportBuilder.cs`, `Hosting/NotificationDispatchService.cs`, `Options/MonitoringOptions.cs`, `Core/Formatting/NotificationFormatter.cs`, `appsettings.json`, `docs/spec.md`, tests.

## 5. New dependencies and infrastructure

| Item | Purpose | Notes |
|---|---|---|
| `WTelegramClient` | MTProto user client | MIT. User-account automation; see risk R1. |
| `Telegram.Bot` | Bot API client | MIT. |
| `Microsoft.EntityFrameworkCore.Sqlite` (+ `.Design`), `dotnet-ef` local tool | Persistence, migrations | |
| `Microsoft.Extensions.Hosting`, `.Hosting.WindowsServices` | Host, Windows Service | |
| `xunit.v3.mtp-v2`, `Shouldly`, `Microsoft.Extensions.TimeProvider.Testing` | Tests | FluentAssertions v8 deliberately avoided (commercial license). Runner: Microsoft.Testing.Platform (`global.json`). Verified in P0: `xunit.v3.mtp-v2` 4.0.2 alone (test projects are `OutputType=Exe`, no `Microsoft.NET.Test.Sdk`, no VSTest adapter), Shouldly 4.3.0. |
| gitleaks (CLI) | Secret scan locally, in the pre-commit hook and in CI | Free; installed via winget locally. P0 uses v8.30.1: `gitleaks git --staged` (staged) and `gitleaks git` (history); CI downloads the same version with checksum check. |
| GitHub Actions | CI | Free for public repos. |
| Telegram `api_id`/`api_hash` | my.telegram.org | Per reader account. |
| Bot via @BotFather, private test group | Delivery | |
| Dedicated phone number | Reader account for the VPS | Owner procures; start warming up during P1a–P5. |
| Linux VPS (~€4–5/month) | Experiment host | Owner procures before P7. Not a paid API, so C6 holds. |
| Secrets | User Secrets (dev), `.env` on VPS | Never committed (C4). |

All package versions: latest stable at P0, pinned in `Directory.Packages.props`.

## 6. Edge cases and risks

| # | Risk / edge case | Mitigation |
|---|---|---|
| R1 | The reader account gets restricted or banned (user-account automation, new account, datacenter IP). | Read-only behaviour; WTelegramClient honours `FLOOD_WAIT`; dedicated, warmed-up account for the VPS; personal account only locally (D14). |
| R2 | Session file leak = full account takeover. | Encrypted with `SessionKey`; `data/` git-ignored; dedicated account on the VPS; volume not shared. |
| R3 | Large channels may deliver updates late, so the latency target is missed for reasons outside our control. | Measured explicitly (`ReceivedAt − PublishedAt`) from P1b; reported per day. This may be the main finding. |
| R4 | Clock skew on the host breaks staleness. | Passive hourly skew estimate from live-message latency (F3.8); NTP in the pre-run checklist. |
| R5 | `КП` is ambiguous (command post, utility company, checkpoint). | Preposition context + case sensitivity; per-pattern precision from labels; one-line disable. |
| R6 | Look-alike Latin characters or transliteration make mentions invisible. | Accepted per D10; detectable via `StoreUnmatchedText` re-scan; normalizer change is small if needed. |
| R7 | Same-name places elsewhere. | Not disambiguated in the MVP; visible as "false" labels. |
| R8 | Full text from `noforwards` sources is reposted (D4). | Test group only (C2); must be revisited before any wider audience. |
| R9 | Storing group messages (personal data). | Switchable text storage, 3-day unmatched retention, 30-day metadata cap, data stays on the owner's VPS. |
| R10 | At-least-once delivery may duplicate a message in the group after an ambiguous timeout. | `PossibleDuplicate` flag; acceptable for the test group. |
| R11 | Burst catch-up after downtime floods the group. | Staleness cutoff + single summary message. |
| R12 | Channel edits a post many times. | Hash comparison + debounce in `ReplyOnChange`. |
| R13 | Bot polling and sending in one process with the same token. | Long polling only for callbacks; no webhook configured; verified in P6a. |
| R14 | WTelegramClient update-state file corrupted → full catch-up or no catch-up. | Staleness protects the group; on load failure, start fresh with a `Warning`. |
| R15 | Regex catastrophic backtracking from a bad config pattern. | 100 ms timeout per match (F5.10). |
| R16 | Public repo exposes which channels are monitored. | Sources and ids only in secrets/env (C4); gitleaks + push protection. |
| R17 | Unverified alerts mislead readers. | Disclaimer on every message (C3); test group only. |
| R18 | Content dedup can hide a repeated alert. | Short window (10 min), cross-source only, duplicates split forwards / same text in the report. |
| R19 | Stale summary or daily report sent twice after an unknown-delivery timeout (send first, mark on success). | Accepted for the test group. |
| R20 | Long processing stall → channel full → spurious reconnects. | `Warning` at > 80% full; WAL + busy timeout keep DB waits short. |
| R21 | A restart within the 15 s `ReplyOnChange` debounce window drops that reply (debounce is in memory). | Accepted; `ReplyOnChange` is not the default mode. |
| R22 | Two processes on one session (`list-chats` while the service runs, a copied session file) → `AUTH_KEY_DUPLICATED`, the session is invalidated, a new login is needed (ban risk). | Session lock file (AC-F1.7, P8); runbook: stop first, delete copies. |
| R23 | A rejected session or a long FLOOD_WAIT plus `restart: unless-stopped` → reconnect loop against Telegram. | Rejection marker (AC-F1.8), FLOOD_WAIT honoured in process (AC-F3.10), reconnect backoff to 10 min (AC-F3.11). |

## 7. Out of scope (feature-wide)

Exactly spec §9: web dashboard, mobile app, HTTP API, LLM/OCR/transcription, geolocation/trajectories/maps, public broadcasting, Latin transliteration and look-alike handling, neighbouring villages, same-name disambiguation, fuzzy dedup, batching matches, `/missed` command, CI deploy, IaC, monitoring stack, MediatR/Polly/repositories/AutoMapper/Redis/brokers.

## 8. Deviation policy

> During implementation, if any phase reveals that the plan is wrong, contradictory, or impossible, the implementing session MUST stop and surface the conflict. Silent rescoping is not allowed. The plan is updated through an explicit revision, not absorbed into a phase.

**Shared wiring files.** Every phase may change `Program.cs` (or DI extension files), `Directory.Packages.props`, `*.csproj`, `appsettings*.json`, `tests/**/Fakes/*` and `.config/dotnet-tools.json`, but only for changes the phase itself needs. A new package must be listed in §5 Dependencies; anything else is a deviation.

## 9. Open questions

| # | Question | Assumption in the meantime |
|---|---|---|
| Q1 | ~~Should metadata be kept 30 days (`MetadataRetentionDays`), or longer, for post-experiment analysis?~~ | Closed: default stays 30; the operator lowers it via config if needed. |
| Q2 | Neighbouring villages as an early signal. | Excluded; add as patterns after a successful demo (D10). |
| Q3 | A `/missed <link>` command to record false negatives. | Not in the MVP; metric 6 uses the stored-text re-scan plus manual review. |
| Q4 | Wording and language of the disclaimer and labels. | Ukrainian, as in spec F8.2 and F10.1; text lives in the formatter and is easy to change. |
| Q5 | Does `ReplyOnChange` become the default after the experiment? | `NotifyOnNewMatch` stays the default. |
| Q6 | Exact list of prepositions for `Abbreviation`. | Spec F5 list; tune via config from labels. |
| Q7 | Policy for `noforwards` sources before any wider audience (D4, R8). | Full text in the test group only. |
| Q8 | Does WTelegramClient's `UpdateManager` state persistence behave as assumed for catch-up across restarts (AC-F3.3)? | Assumed yes; verified in P1b, including the save policy (state saved only with an empty channel) and the P7 `docker kill` scenario. If not, surface it as a deviation. |

## 10. Review decisions

Architect review of 2026-10-26, discussed item by item with the owner on 2026-10-26/27. Every finding was approved and is applied above; none was rejected. Decisions a later reviewer might otherwise re-raise:

- Phases P1, P3, P4, P6 are split with `a`/`b` suffixes so P2, P5 and P7 keep their numbers.
- A labeled message that matched several patterns counts toward the precision of each of them; the daily report says so.
- Content-hash dedup uses its own short window (10 min) and only across sources, so a repeated alert in one channel is always sent.
- Stale summary and daily report are not outbox rows; a duplicate after an unknown-delivery timeout is accepted (R19).
- `ReplyOnChange` debounce stays in memory; a restart inside the window drops the reply (R21).
- Clock skew is estimated passively from live messages; no `Help_GetConfig` call, to avoid extra requests from the user account.
- `Storage:UnmatchedRetentionDays` stays 3 in the spec; the experiment sets 8 via config so metric 6 can be measured with `rescan`.
- `MetadataRetentionDays` stays 30 (Q1 closed).
