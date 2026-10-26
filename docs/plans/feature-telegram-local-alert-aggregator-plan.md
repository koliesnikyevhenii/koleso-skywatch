# Plan: Telegram Local Alert Aggregator (Skywatch MVP)

| | |
|---|---|
| Spec | [docs/spec.md](../spec.md) — acceptance criteria referenced as `AC-Fx.y` |
| Source idea | [docs/source/telegram_local_alert_aggregator_feature_plan.md](../source/telegram_local_alert_aggregator_feature_plan.md) |
| Created | 2026-10-26 |
| Phases | P0–P7 (8 phases, one Claude session each) |

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
├─ Skywatch.slnx · global.json · Directory.Build.props · Directory.Packages.props · .editorconfig
├─ Dockerfile · .dockerignore
├─ src/
│  ├─ Skywatch.Core/            pure logic, no IO packages (C7)
│  │  ├─ Normalization/         TextNormalizer, NormalizedText
│  │  ├─ Filtering/             KeywordFilter, PatternDefinition, FilterResult, PatternHit
│  │  ├─ Deduplication/         ContentHasher, OriginKey
│  │  ├─ Formatting/            NotificationFormatter, SourceLinkBuilder, DailyReportFormatter
│  │  └─ Time/                  StalenessPolicy, ReportPeriod
│  └─ Skywatch.Worker/
│     ├─ Program.cs             host + run-mode dispatch (login | list-chats | run)
│     ├─ Options/               TelegramOptions, MonitoringOptions, NotificationOptions, ReportingOptions, StorageOptions + validators
│     ├─ Telegram/              ITelegramUpdateSource, WTelegramUpdateSource, TlMapper, LoginCommand, ListChatsCommand,
│     │                         INotificationSender, BotNotificationSender, BotCallbackListener
│     ├─ Persistence/           SkywatchDbContext, Entities/, Migrations/, UtcDateTimeConverter
│     ├─ Pipeline/              MessageProcessor, DuplicateDetector, EditHandler, LabelService, DailyReportBuilder
│     └─ Hosting/               ListenerService, ProcessingService, NotificationDispatchService,
│                               StaleSummaryService, EditReplyService, DailyReportService, RetentionService
├─ tests/
│  ├─ Skywatch.Core.Tests/
│  └─ Skywatch.Worker.Tests/    (Fakes/: FakeUpdateSource, FakeNotificationSender; SqliteTestDb)
├─ build/verify.ps1
├─ .claude/skills/verify/SKILL.md
├─ .github/workflows/ci.yml
├─ deploy/                      docker-compose.yml, .env.example, README (runbook)
└─ docs/                        spec.md, plans/, source/
```

**Runtime flow**

```text
WTelegramClient UpdateManager
   └─► WTelegramUpdateSource (allowlist, TlMapper → IncomingMessage)
          └─► Channel<IncomingMessage>(1000, Wait)
                 └─► ProcessingService → MessageProcessor
                        normalize → filter → persist (idempotent) → dedup → staleness → Notification(Pending)   [one transaction]
                                                                                   └─► Channel<long notificationId>
                                                                                          └─► NotificationDispatchService → INotificationSender (Telegram.Bot)
BotCallbackListener (long polling) ─► LabelService
StaleSummaryService (30 s) · EditReplyService (debounce) · DailyReportService (09:00 Kyiv) · RetentionService (hourly)
```

**Key design choices**

- **Outbox.** A `Pending` row is written in the same transaction as the message. The dispatch channel is only a wake-up signal, so on restart, `Pending` rows are reloaded (AC-F8.11).
- **Two Telegram seams only:** `ITelegramUpdateSource` and `INotificationSender` (D13). TL-type mapping lives in static `TlMapper` functions, so it can be unit-tested with hand-built TL objects.
- **Single-writer processing:** one consumer per channel avoids SQLite write contention and keeps ordering.
- **Options pattern** with `ValidateOnStart`. Regex patterns are compiled once into a singleton `KeywordFilter`.
- **No** MediatR, Polly, repository layer, AutoMapper or Serilog (spec §9).

## 4. Phases of work

Each phase ends with `/verify` green and a commit. Tests are named after the AC they prove (e.g. `AC_F5_3_abbreviation_after_preposition_matches`).

---

### P0 — Skeleton and gate chain

**Scope**
- `global.json` (SDK 10.0.401, `rollForward: latestFeature`), `Directory.Build.props` (net10.0, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`), `Directory.Packages.props` (CPM), `.editorconfig`.
- `Skywatch.slnx` with the four projects. `Skywatch.Worker` is a minimal Worker host that starts, logs "Skywatch started" and stops cleanly. Project references: Worker → Core, test projects → their targets.
- One placeholder test per test project (xUnit v3 + Shouldly), so the test stage runs.
- `build/verify.ps1`: `dotnet restore` → `dotnet build -c Release` → `dotnet test -c Release --no-build` → `dotnet format --verify-no-changes` → `gitleaks detect --no-banner --redact`. Stops at the first failing stage with a non-zero exit code.
- `.claude/skills/verify/SKILL.md` wrapping the script; fill the Verification section of `CLAUDE.md`.
- `.github/workflows/ci.yml`: push/PR to `develop` on `ubuntu-latest`, setup-dotnet from `global.json`, same stages, gitleaks via CLI.
- `.gitignore` additions: `data/`, `*.db*`, `*.session`, `*.state`, `appsettings.*.local.json`, `appsettings.Production.json`.
- `Skywatch.Worker` gets `UserSecretsId`.

**Out of scope:** any Telegram, EF or domain code.

**Acceptance criteria**
1. `pwsh build/verify.ps1` passes on a clean clone on Windows.
2. The CI workflow passes on GitHub for the P0 commit.
3. A committed fake token pattern (tested locally on a throwaway branch, never pushed) makes gitleaks fail `/verify`.
4. `dotnet run --project src/Skywatch.Worker` starts and stops on Ctrl+C with exit code 0.
5. `Skywatch.Core.csproj` has no package references.

**Tests-first list**
- Placeholder test in each test project (proves the test stage runs).
- (Manual) gitleaks negative check, from criterion 3.

**Files:** all of the above. The contract exposed is the `/verify` entry point.

**Manual steps for the owner:** enable GitHub Secret Scanning + Push Protection; `winget install gitleaks`.

---

### P1 — Telegram connectivity

**Scope**
- `TelegramOptions`, `MonitoringOptions.Sources` with validation. User Secrets wiring. `appsettings.json` with empty identifying values, plus `appsettings.Example.json`.
- Run-mode dispatch in `Program.cs`: `login`, `list-chats`, default run.
- `LoginCommand` (AC-F1.1, F1.4), `ListChatsCommand` (AC-F1.2), session-missing behaviour (AC-F1.3).
- `ITelegramUpdateSource`, `IncomingMessage`, `OriginKey`. `WTelegramUpdateSource` using `UpdateManager` with state file `Telegram:UpdateStatePath`. Source resolution at startup (AC-F2.2 without DB upsert, F2.3, F2.4), allowlist filter (F2.5), bounded channel (F3.4).
- `TlMapper` static functions: text/caption extraction (F3.5), forward origin (F3.6), marked chat id ↔ peer id conversion, edit mapping (F3.9).
- Temporary `ProcessingService` that logs each `IncomingMessage` safely: source name, message id, text length (not text), and `ReceivedAt − PublishedAt` latency.
- Clock offset check (F3.8). Disconnect/reconnect logging (F3.7, log only).
- WTelegramClient's own logging routed into `ILogger` at `Warning`+, with secrets filtered (F1.5).

**Out of scope:** normalization, filtering, persistence, the bot.

**Acceptance criteria**
1. AC-F1.1–F1.5, AC-F2.1, F2.3–F2.5, AC-F3.1–F3.6, F3.8, F3.9 hold.
2. Manual: with the personal account and 2–3 test channels, a new post appears in the log within seconds; restarting does not print old posts from before the first start; a post published while the service was stopped is printed after restart, with its latency.
3. Manual: `list-chats` output is enough to fill `Monitoring:Sources` without other tools.

**Tests-first list**
- `TlMapper`: text message → `IncomingMessage` with correct ids and UTC times; media with caption → caption used; media without caption → null (discarded); forwarded channel post → origin key set; non-forward → origin = self; `UpdateEditChannelMessage` → `IsEdit` + `EditedAtUtc`.
- Marked id conversion: channel `-100xxxxxxxxxx` ↔ channel id; basic group `-xxxx` ↔ chat id; round trip.
- Allowlist: an update from a non-allowlisted chat is not written to the channel (F2.5).
- Options validation: missing `ApiId`/`ApiHash`/`SessionKey` in run mode fails startup; `login` mode does not require `SessionKey` to be pre-existing on disk.
- Run without a session file → exit code ≠ 0 and a `Critical` log containing "login" (F1.3).
- Log capture with known secret values: no secret substring appears (F1.5).
- Clock offset > 10 s → `Warning` (with `FakeTimeProvider`).

**Files:** `Program.cs`; `Options/TelegramOptions.cs`, `Options/MonitoringOptions.cs` (+ validators); `Telegram/ITelegramUpdateSource.cs`, `WTelegramUpdateSource.cs`, `TlMapper.cs`, `LoginCommand.cs`, `ListChatsCommand.cs`; `Hosting/ListenerService.cs`, `Hosting/ProcessingService.cs` (temporary logger body); `Core/Deduplication/OriginKey.cs`; `appsettings.json`, `appsettings.Example.json`; tests.
**Contracts:** `ITelegramUpdateSource.ReadAllAsync(CancellationToken) : IAsyncEnumerable<IncomingMessage>`; `IncomingMessage` record (spec F3).

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
- A real-world sample set in `tests/Skywatch.Core.Tests/Samples/` (anonymized alert texts written for tests, not copied from channels), with expected pattern names.

**Files:** `Core/Normalization/*`, `Core/Filtering/*`, `Worker/Options/MonitoringOptions.cs` (patterns + validator), `Worker/Hosting/ProcessingService.cs`, `appsettings.json`, Core tests.
**Contracts:** `TextNormalizer.Normalize(string?) : NormalizedText`; `KeywordFilter.Match(NormalizedText) : FilterResult`.

---

### P3 — Persistence, deduplication, retention

**Scope**
- EF Core SQLite: `SkywatchDbContext`, entities `Source`, `Message`, `Notification`, `ServiceEvent` (spec §7), `UtcDateTimeConverter`, indexes, initial migration, migrate at startup. `dotnet-ef` as a local tool (`.config/dotnet-tools.json`).
- `StorageOptions`. Source upsert at startup (completes AC-F2.2).
- `ContentHasher` (F6.3). `DuplicateDetector` (F6.4).
- `MessageProcessor`: idempotent insert (F6.2), text-storage rule (F6.1), duplicate marking (F6.5), `Pending` notification in the same transaction (F6.6) or log-only when disabled (F6.7), error isolation (F6.8). Staleness check at creation via `StalenessPolicy` (F7.1 creation-time part only, status `SkippedStale`).
- `ServiceEvents` for Started/Stopped/Disconnected/Reconnected/ProcessingError (completes F3.7).
- `RetentionService` (F12.1–F12.4).
- `ProcessingService` delegates to `MessageProcessor`. New-message events only; edit events are logged and ignored until P5.

**Out of scope:** sending anything; edits.

**Acceptance criteria:** AC-F2.2, F3.7, F6.1–F6.8, F7.1 (creation time), F7.2, F12.1–F12.4. Manual: after a day of local running, `data/skywatch.db` holds metadata rows for all received messages and text only for matches when `StoreUnmatchedText=false`.

**Tests-first list** (Worker.Tests, SQLite in-memory, `FakeTimeProvider`)
- Same `(source, msgId)` processed twice → one row, one notification (F6.2), including a concurrent double-insert surfacing as a unique-constraint violation that is handled as "already processed".
- Unmatched + `StoreUnmatchedText=true` → text stored; `=false` → text null, metadata present.
- Hash: known input → known SHA-256 hex; two texts differing only by zero-width chars or whitespace → same hash.
- Duplicate by origin key: original then forward within window → forward `IsDuplicate`, `SkippedDuplicate`. Forward first, then original → original is the duplicate.
- Duplicate by hash across two sources within window; same pair outside the window → both unique.
- A duplicate of a duplicate points to the first non-duplicate.
- Notifications disabled → no notification rows; match logged.
- Stale at creation (age 121 s) → `SkippedStale`; age 120 s → `Pending`.
- Exception in processing one message → `ProcessingError` event and the next message is processed.
- Retention at boundaries for each of the three rules; service events are deleted too.
- `UtcDateTimeConverter`: value read back has `Kind=Utc`; range queries on `ReceivedAtUtc` are translated to SQL (no client evaluation exception).
- Source upsert: new, changed name, disabled in config.

**Files:** `Worker/Persistence/**`, `Worker/Pipeline/MessageProcessor.cs`, `DuplicateDetector.cs`, `Core/Deduplication/ContentHasher.cs`, `Core/Time/StalenessPolicy.cs`, `Worker/Options/StorageOptions.cs`, `Worker/Hosting/RetentionService.cs`, `ProcessingService.cs`; tests.
**Contracts:** `MessageProcessor.ProcessAsync(IncomingMessage, CancellationToken) : Task<ProcessOutcome>`; `StalenessPolicy.IsStale(DateTime eventTimeUtc, DateTime nowUtc)`.

---

### P4 — Notification delivery

**Scope**
- `NotificationOptions`. `INotificationSender`, `BotNotificationSender` (Telegram.Bot, plain text, no parse mode).
- `NotificationFormatter` (F8.2, F8.4, F8.5, F8.12), `SourceLinkBuilder` (F8.3).
- `NotificationDispatchService`: channel of notification ids, single consumer, startup recovery of `Pending` (F8.1, F8.11), staleness recheck before each attempt (F7.1), 429 handling (F8.6), retry with backoff (F8.7), fatal 4xx (F8.8), `PossibleDuplicate` (F8.9), success bookkeeping (F8.10).
- `StaleSummaryService` (F7.4).
- `MessageProcessor` signals the dispatch channel after commit.
- Buttons are NOT attached yet (P6).

**Out of scope:** edits, labeling, daily report.

**Acceptance criteria:** AC-F7.1 (all three check points), F7.3 (formatter/age part), F7.4, F8.1–F8.13. Manual: with notifications enabled, a test post containing `на КП` in a test channel appears in the test group within ~5 s in the agreed format; a post forwarded to a second test channel does not produce a second message.

**Tests-first list**
- Formatter: exact output for a public channel, a private channel, and a basic group (no link line); pattern tags; edit marker; Kyiv time across the 25 Oct 2026 DST change (summer vs winter time); latency rounding; 5000-char text truncated with `…` and total ≤ 4096; header/footer intact.
- Link builder: username link; `t.me/c/{id}` from a `-100…` marked id; basic group → null.
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
- Stale summary: 3 unreported stale → one summary with N=3 and time range, all marked; nothing unreported → nothing sent.
- End-to-end pipeline test: `FakeUpdateSource` → processor → dispatcher → `FakeNotificationSender`, covering match, duplicate, stale, non-match.

**Files:** `Worker/Telegram/INotificationSender.cs`, `BotNotificationSender.cs`; `Core/Formatting/NotificationFormatter.cs`, `SourceLinkBuilder.cs`; `Worker/Hosting/NotificationDispatchService.cs`, `StaleSummaryService.cs`; `Worker/Options/NotificationOptions.cs`; `MessageProcessor.cs` (signal); tests + fakes.
**Contracts:** `INotificationSender.SendAsync(OutgoingNotification, CancellationToken) : Task<SendResult>`, `ReplyAsync(long replyToBotMessageId, OutgoingNotification, CancellationToken)`; `SendResult` = Success(botMessageId) | RateLimited(retryAfter) | Transient(error, deliveryUnknown) | Fatal(error).

---

### P5 — Edited messages

**Scope**
- `EditHandler` with the three `EditHandling` modes (F9.1–F9.6). Uses existing normalize/filter/dedup/staleness (edit time) and outbox.
- `Notification.Kind` = `EditNewMatch` / `EditReply`.
- `EditReplyService`: debounce per message (`EditDebounceSeconds`), then a reply through `INotificationSender.ReplyAsync` to the original `BotMessageId`.
- Remove the P3 "edits ignored" stub from `ProcessingService`.

**Out of scope:** labeling (edit replies get no buttons).

**Acceptance criteria:** AC-F9.1–F9.6, AC-F7.3. Manual: editing a test-channel post to add `Краснопавлівка` produces a `✏️ оновлено` notification within ~5 s.

**Tests-first list**
- `Ignore`: edit adding a mention → nothing changes.
- `NotifyOnNewMatch`: unmatched → matched edit → `EditNewMatch` notification with marker; matched → matched edit → none; unknown message edit that matches → notification; edit whose new text duplicates another recent match → `SkippedDuplicate`; edit with `EditedAt` 121 s ago → `SkippedStale`; edit with identical hash → ignored.
- `ReplyOnChange`: sent message edited 3 times within 10 s → exactly one reply 15 s after the last edit, with the final text; edit to a not-yet-sent (`Pending`) message → no reply; edit when `BotMessageId` is null → no reply, `Warning`.
- Text-storage rule applies to edits (F9.2 with `StoreUnmatchedText=false`).

**Files:** `Worker/Pipeline/EditHandler.cs`, `Worker/Hosting/EditReplyService.cs`, `ProcessingService.cs`, `NotificationFormatter.cs` (reply variant), `Persistence` (Kind enum, migration if needed); tests.
**Contracts:** `EditHandler.HandleAsync(IncomingMessage, CancellationToken)`.

---

### P6 — Labeling and daily report

**Scope**
- Inline keyboard on `New` / `EditNewMatch` notifications (F10.1).
- `BotCallbackListener` hosted service: long polling for `callback_query` only (F10.6). It must not conflict with the sending client (same bot token; there is no webhook).
- `LabelService` (F10.2–F10.5).
- `ReportingOptions`, `ReportPeriod` (Kyiv day bounds with DST), `DailyReportBuilder` (queries), `DailyReportFormatter`, `DailyReportService` with catch-up and once-per-day guarantee via a `DailyReportSent` service event (F11.1–F11.5).

**Out of scope:** `/missed` command, charts, CSV export.

**Acceptance criteria:** AC-F10.1–F10.6, AC-F11.1–F11.5. Manual: pressing buttons in the test group updates the database; the 09:00 report appears with plausible numbers.

**Tests-first list**
- Callback-data parse: valid `lbl:42:u|f|d`; malformed; unknown id.
- Authorized press → label stored; buttons re-rendered with `✔`; a second press with a different label overwrites.
- Unauthorized press → nothing stored; answer `Немає прав`.
- `ReportPeriod` for 2026-10-25 Kyiv → 25 hours; 2026-03-29 → 23 hours; a normal day → 24 hours; bounds in UTC.
- Report builder on seeded data: counts per pattern, duplicates, statuses, labels, precision overall and per pattern, `n/a` with zero labels, p50/p95 on a known distribution, reconnect count and total disconnected time from events (including a disconnect still open at the period end).
- Scheduler with `FakeTimeProvider`: fires at 09:00 local; service down at 09:00 and started at 11:00 → sends once; restart after sending → does not resend.
- Notifications disabled → report goes to the log.

**Files:** `Worker/Telegram/BotCallbackListener.cs`, `BotNotificationSender.cs` (keyboard, edit markup, answer callback); `Worker/Pipeline/LabelService.cs`, `DailyReportBuilder.cs`; `Core/Formatting/DailyReportFormatter.cs`; `Core/Time/ReportPeriod.cs`; `Worker/Hosting/DailyReportService.cs`; `Worker/Options/ReportingOptions.cs`; tests.
**Contracts:** `INotificationSender` gains `AnswerCallbackAsync`, `SetLabelMarkupAsync`; `LabelService.ApplyAsync(long notificationId, Label, long userId, CancellationToken) : Task<LabelOutcome>`.

---

### P7 — Deployment

**Scope**
- `Dockerfile` at root: multi-stage (`sdk:10.0` → `runtime:10.0` or `aspnet`-free `runtime-deps` if self-contained is chosen), non-root user, `/app/data` volume, `TZ` not required (time zone comes from config).
- `.dockerignore`; `deploy/docker-compose.yml` with `restart: unless-stopped`, named volume for `data/`, `env_file: .env`; `deploy/.env.example` with every variable name and no values.
- JSON console logging in containers (N6), log rotation through Docker's `json-file` options.
- Graceful shutdown: `HostOptions.ShutdownTimeout = 30 s`, channel drain, `Stopped` event (N4).
- `UseWindowsService()` for the Windows path.
- `deploy/README.md` runbook: VPS provisioning (minimal), `login` with the dedicated account, session transfer (`session.dat` + same `SessionKey`), `list-chats`, filling `.env`, start/stop/logs, DB backup/copy for analysis, how to run the stale-text re-scan for metric 6, Windows Service install (`sc.exe create`).
- Pre-run checklist: dedicated account warmed up, bot in group, `Notifications:Enabled=true`, `StoreUnmatchedText=true`, NTP enabled on the VPS.

**Out of scope:** CI deploy, IaC, monitoring stack.

**Acceptance criteria**
1. `docker build .` succeeds; the container runs as non-root.
2. `docker compose up -d` with a valid `.env` and a copied session runs the pipeline; `docker compose restart` keeps the session and DB, and `Pending` notifications survive.
3. `docker stop` produces a `Stopped` event and exits within 30 s.
4. Running `login` interactively in the container (`docker compose run --rm skywatch login`) works as an alternative to copying a session.
5. The runbook is followed end to end once on the actual VPS before the experiment starts.

**Tests-first list**
- Graceful shutdown test: host with `FakeUpdateSource` holding queued messages, stop requested → all queued messages processed or still `Pending`, `Stopped` event written.
- Options binding from environment variables (`Notifications__BotToken` etc.) test.
- (Manual) criteria 1–5.

**Files:** `Dockerfile`, `.dockerignore`, `deploy/*`, `Program.cs` (Windows service, shutdown, JSON logging), tests.

## 5. New dependencies and infrastructure

| Item | Purpose | Notes |
|---|---|---|
| `WTelegramClient` | MTProto user client | MIT. User-account automation; see risk R1. |
| `Telegram.Bot` | Bot API client | MIT. |
| `Microsoft.EntityFrameworkCore.Sqlite` (+ `.Design`), `dotnet-ef` local tool | Persistence, migrations | |
| `Microsoft.Extensions.Hosting`, `.Hosting.WindowsServices` | Host, Windows Service | |
| `xunit.v3`, `Shouldly`, `Microsoft.Extensions.TimeProvider.Testing` | Tests | FluentAssertions v8 deliberately avoided (commercial license). |
| gitleaks (CLI) | Secret scan locally and in CI | Free; installed via winget locally. |
| GitHub Actions | CI | Free for public repos. |
| Telegram `api_id`/`api_hash` | my.telegram.org | Per reader account. |
| Bot via @BotFather, private test group | Delivery | |
| Dedicated phone number | Reader account for the VPS | Owner procures; start warming up during P1–P5. |
| Linux VPS (~€4–5/month) | Experiment host | Owner procures before P7. Not a paid API, so C6 holds. |
| Secrets | User Secrets (dev), `.env` on VPS | Never committed (C4). |

All package versions: latest stable at P0, pinned in `Directory.Packages.props`.

## 6. Edge cases and risks

| # | Risk / edge case | Mitigation |
|---|---|---|
| R1 | The reader account gets restricted or banned (user-account automation, new account, datacenter IP). | Read-only behaviour; WTelegramClient honours `FLOOD_WAIT`; dedicated, warmed-up account for the VPS; personal account only locally (D14). |
| R2 | Session file leak = full account takeover. | Encrypted with `SessionKey`; `data/` git-ignored; dedicated account on the VPS; volume not shared. |
| R3 | Large channels may deliver updates late, so the latency target is missed for reasons outside our control. | Measured explicitly (`ReceivedAt − PublishedAt`) from P1; reported per day. This may be the main finding. |
| R4 | Clock skew on the host breaks staleness. | Offset check vs server time (F3.8); NTP in the pre-run checklist. |
| R5 | `КП` is ambiguous (command post, utility company, checkpoint). | Preposition context + case sensitivity; per-pattern precision from labels; one-line disable. |
| R6 | Look-alike Latin characters or transliteration make mentions invisible. | Accepted per D10; detectable via `StoreUnmatchedText` re-scan; normalizer change is small if needed. |
| R7 | Same-name places elsewhere. | Not disambiguated in the MVP; visible as "false" labels. |
| R8 | Full text from `noforwards` sources is reposted (D4). | Test group only (C2); must be revisited before any wider audience. |
| R9 | Storing group messages (personal data). | Switchable text storage, 3-day unmatched retention, 30-day metadata cap, data stays on the owner's VPS. |
| R10 | At-least-once delivery may duplicate a message in the group after an ambiguous timeout. | `PossibleDuplicate` flag; acceptable for the test group. |
| R11 | Burst catch-up after downtime floods the group. | Staleness cutoff + single summary message. |
| R12 | Channel edits a post many times. | Hash comparison + debounce in `ReplyOnChange`. |
| R13 | Bot polling and sending in one process with the same token. | Long polling only for callbacks; no webhook configured; verified in P6. |
| R14 | WTelegramClient update-state file corrupted → full catch-up or no catch-up. | Staleness protects the group; on load failure, start fresh with a `Warning`. |
| R15 | Regex catastrophic backtracking from a bad config pattern. | 100 ms timeout per match (F5.10). |
| R16 | Public repo exposes which channels are monitored. | Sources and ids only in secrets/env (C4); gitleaks + push protection. |
| R17 | Unverified alerts mislead readers. | Disclaimer on every message (C3); test group only. |

## 7. Out of scope (feature-wide)

Exactly spec §9: web dashboard, mobile app, HTTP API, LLM/OCR/transcription, geolocation/trajectories/maps, public broadcasting, Latin transliteration and look-alike handling, neighbouring villages, same-name disambiguation, fuzzy dedup, batching matches, `/missed` command, CI deploy, IaC, monitoring stack, MediatR/Polly/repositories/AutoMapper/Redis/brokers.

## 8. Deviation policy

> During implementation, if any phase reveals that the plan is wrong, contradictory, or impossible, the implementing session MUST stop and surface the conflict. Silent rescoping is not allowed. The plan is updated through an explicit revision, not absorbed into a phase.

## 9. Open questions

| # | Question | Assumption in the meantime |
|---|---|---|
| Q1 | Should metadata be kept 30 days (`MetadataRetentionDays`), or longer, for post-experiment analysis? | 30 days. Not discussed explicitly; chosen so labels and stats outlive the 7-day text retention. |
| Q2 | Neighbouring villages as an early signal. | Excluded; add as patterns after a successful demo (D10). |
| Q3 | A `/missed <link>` command to record false negatives. | Not in the MVP; metric 6 uses the stored-text re-scan plus manual review. |
| Q4 | Wording and language of the disclaimer and labels. | Ukrainian, as in spec F8.2 and F10.1; text lives in the formatter and is easy to change. |
| Q5 | Does `ReplyOnChange` become the default after the experiment? | `NotifyOnNewMatch` stays the default. |
| Q6 | Exact list of prepositions for `Abbreviation`. | Spec F5 list; tune via config from labels. |
| Q7 | Policy for `noforwards` sources before any wider audience (D4, R8). | Full text in the test group only. |
| Q8 | Does WTelegramClient's `UpdateManager` state persistence behave as assumed for catch-up across restarts (AC-F3.3)? | Assumed yes; verified in P1. If not, surface it as a deviation. |
