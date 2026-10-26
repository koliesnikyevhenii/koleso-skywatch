# Review decisions: feature-telegram-local-alert-aggregator-plan

Source: architect review of `feature-telegram-local-alert-aggregator-plan.md` (2026-10-26), discussed item by item with the owner on 2026-10-26/27. Every item below is **approved**. Apply them in a separate session as one explicit plan revision (deviation policy §8), plus the listed `docs/spec.md` and `CLAUDE.md` changes.

Documents affected: the plan, `docs/spec.md` (F1, F1.5, F3.8, F6.4, §7, §8, F11.2, §10), `CLAUDE.md`.

---

## Blockers

**B1. Shared "wiring files" are in scope for every phase.**
Add to plan §8: every phase may change `Program.cs` (or DI extension files), `Directory.Packages.props`, `*.csproj`, `appsettings*.json`, `tests/**/Fakes/*`, `.config/dotnet-tools.json`, but only for changes the phase itself needs. A new package must be listed in §5 Dependencies; anything else is a deviation.

**B2. Split the CLAUDE.md "never commit or log" rule (spec unchanged).**
- Never **commit** (C4): secrets and identifying values (the current list).
- Never **log** (C5): secrets only: api hash, phone, auth code, 2FA, session key/file, bot token. Source names, chat ids and labeler ids are allowed in operator logs (local / VPS, not git).
- Add: tests and CI use only fake ids, so public CI output reveals nothing.

**B3. `NotificationOptions` starts in P3.**
- P3 (now P3b, see M1) creates `NotificationOptions` with `Enabled` and `MaxMessageAgeSeconds` and a validator for them (`MaxMessageAgeSeconds > 0`). Add the file to that phase's list.
- P4a extends it: `BotToken`, `TargetChatId`, other send settings, and the conditional validation from spec §8. File list entry: `NotificationOptions.cs (extend)`.
- Note in P3: with `Enabled = true`, `Pending` rows are created but nothing sends them until P4. This is intended; P4 picks them up via startup recovery (F8.1).

---

## Major

**M1. Split oversized phases (suffix numbering, P2/P5/P7 keep their numbers).**

| Phase | a | b |
|---|---|---|
| P1 | **Login & config:** options + validation, run-mode dispatch, `login`, `list-chats`, F1.3, WTelegramClient log bridge with secret filtering (F1.5) | **Listener:** `ITelegramUpdateSource`, `TlMapper`, `UpdateManager` + state file, source resolution, allowlist, bounded channel, temporary `ProcessingService`, clock-skew check, reconnect logging |
| P3 | **Storage:** DbContext, entities, `UtcDateTimeConverter`, full initial migration, migrate at startup, WAL, source upsert, `ServiceEvents`, `RetentionService` | **Pipeline:** `ContentHasher`, `DuplicateDetector`, `StalenessPolicy`, `MessageProcessor`, wiring into `ProcessingService`, `NotificationOptions` (B3) |
| P4 | **Sender:** `NotificationFormatter`, `SourceLinkBuilder` (pure Core), `INotificationSender`, `BotNotificationSender` (send gate, M5), `SendResult`, `NotificationOptions` extension, time-zone resolver (M10) | **Dispatcher:** `NotificationDispatchService` (recovery, 429, retry, staleness recheck), `StaleSummaryService`, processor signal, end-to-end test |
| P6 | **Labeling:** keyboard, `BotCallbackListener`, `LabelService` | **Report & rescan:** `ReportingOptions`, `ReportPeriod`, builder, formatter, `DailyReportService`, `rescan` mode (M6) |

After the revision, run `/ideo:plan-validate` on the plan.

**M2. `UpdateManager` state save policy (P1b).**
- On shutdown: stop reading new updates → drain the channel through processing → save state.
- Periodically: every `Telegram:UpdateStateSaveIntervalSeconds` (default 60), **only when the channel is empty and nothing is being processed** (mechanics: see m10).
- State explicitly: after a crash, replay from the last saved state is possible and intended; F6.2 (idempotent insert) and staleness make it safe.
- Tests: P1b unit test, no save while the channel is non-empty, save when empty. P7 manual check: `docker kill` during traffic → restart → nothing lost, nothing duplicated in the group.
- Q8 stays open; its P1b verification now includes this scenario.

**M3. Full seam contract defined in P1b.**
```csharp
interface ITelegramUpdateSource
{
    Task<IReadOnlyList<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct);
    IAsyncEnumerable<SourceEvent> ReadAllAsync(CancellationToken ct);
    Task<bool> TrySaveStateAsync(CancellationToken ct);   // m10
}

abstract record SourceEvent;
record IncomingMessage(...) : SourceEvent;                       // spec F3
record ConnectionStateChanged(ConnectionState State, DateTime AtUtc) : SourceEvent;

record ResolvedSource(long ChatId, string Name, SourceType Type,
                      string? Username, bool NoForwards, bool Resolved);
```
- Startup: `ResolveSourcesAsync` → P1b logs it (F2.3/F2.4), P3a upserts → then `ReadAllAsync`.
- Connection events flow through the same channel as messages (order kept, single DB writer).
- Contract records live in `Worker/Telegram/` next to the interface; no TL types in them.
- `FakeUpdateSource` (P1b) can emit messages, connection events, a configured `ResolvedSource` list, and records `TrySaveStateAsync` calls.

**M4. SQLite concurrency (P3a).**
- `PRAGMA journal_mode=WAL` once at startup after migrate.
- Busy timeout via connection string `Default Timeout`, configurable as `Storage:BusyTimeoutSeconds` (default 30).
- `IDbContextFactory<SkywatchDbContext>`; a new context per unit of work (one message, one send attempt, one retention batch, one button press). Never inject `DbContext` into singleton services. Add this line to CLAUDE.md Conventions.
- Retention deletes in batches of `Storage:RetentionBatchSize` (default 500), each in its own short transaction.
- Rewrite plan §3: "many writers, short transactions; contention handled by WAL + busy timeout".
- Tests: main tests remain SQLite in-memory; one smoke test on a temp file DB (batched retention and concurrent inserts → no `SqliteException`); F6.2 test: after a unique violation, the next message is processed (fresh context).

**M5. All bot sends are coordinated; alerts have priority.**
- **Edit replies** go through the outbox: `EditReplyService` only debounces, then creates `Notification(Kind=EditReply, Pending)` and signals the dispatcher. The dispatcher calls `ReplyAsync(originalBotMessageId, …)` with the same retry / 429 / `PossibleDuplicate` / staleness recheck (age from `EditedAtUtc`, F7.3). Phase P5.
- **Stale summary (P4b) and daily report (P6b)** are not outbox rows. Order: send first, mark only on success (`StaleReportedAtUtc` / `DailyReportSent` event). On failure: summary retries on the next 30 s tick; report retries with backoff until success. A duplicate summary/report on unknown-delivery timeout is accepted; add it to the risk table.
- **Send gate** in `BotNotificationSender` (P4a): serializes all bot sends; on 429 stores `blockedUntil` and every sender waits until then (via `TimeProvider`); **alerts and edit replies have priority**, so summary and report are sent only when no alert is waiting.
- Tests: after a dispatcher 429, a summary send waits for `retry_after`; an alert and a report ready at the same time → the alert is sent first.

**M6. `rescan` run mode for metric 6 (P6b).**
- `dotnet run --project src/Skywatch.Worker -- rescan [--pattern "<regex>"]`: reads unmatched messages with stored text, runs the current config patterns (or a one-off regex), prints date, source, message id, matching pattern, and a text snippet to the console.
- Read-only: sends nothing, writes nothing, no migrations.
- P7 pre-run checklist: `Storage:UnmatchedRetentionDays = 8` (experiment length + 1) during the experiment. Spec default stays 3.
- Spec: add `rescan` to the run modes (F1) and reference it in §10 metric 6. Not covered by the §9 exclusions (`/missed` bot command and CSV export only).
- Runbook (P7) describes the command.

**M7. Content-hash dedup must not hide repeated alerts.**
- Separate windows: `Monitoring:DuplicateWindowMinutes = 60` for origin-key (forward) duplicates only; new `Monitoring:ContentDuplicateWindowMinutes = 10` for identical text.
- New toggle `Monitoring:ContentDuplicateAcrossSourcesOnly = true`: identical text is a duplicate only if it came from a **different** source; a repeat in the same source is always sent.
- Daily report splits duplicates: forwards N / same text M.
- Risk table: "content dedup can hide a repeated alert; mitigated by short window, cross-source only, split counts in report".
- Spec changes: F6.4, §8 config, F11.2. Code: P3b (detector), P6b (report). Schema: `Messages.DuplicateReason` (m4).

**M8. Secret scanning actually covers what is about to be committed (P0).**
1. `/verify` scans staged changes and history (choose the exact command for the gitleaks version installed in P0; syntax changed from `protect --staged` to `git --staged`).
2. `.gitleaks.toml` custom rule for `-100\d{10}` channel ids in tracked files; tests use obviously fake ids (e.g. `-1000000000001`) that are allowlisted.
3. Test: committed `appsettings.json` has empty `Sources`, `TargetChatId`, `LabelerUserIds`.
4. Add `appsettings.Development.json` to `.gitignore`.
5. CI checkout with `fetch-depth: 0`.
6. Git pre-commit hook running gitleaks on staged changes; install script in `build/`.

**M9. Test patterns as loaded from the real `appsettings.json` (P2).**
Worker test: loads the project's `appsettings.json` the same way the host does, binds `Monitoring:Patterns`, runs the F5.1 and F5.3 positive and negative samples. Core tests and this test share one sample set (`tests/.../Samples/`). Guards against JSON escaping errors (`"\b"` = backspace).

**M10. Time-zone resolution is verified, not assumed.**
- Startup resolver (Worker, P4a): try `Europe/Kyiv`, fall back to `Europe/Kiev`; if neither resolves, fail startup with a clear error. Log the resolved zone and current Kyiv time. Core receives a `TimeZoneInfo` (C7: no IO in Core).
- Test: resolver with a fake lookup; `Kyiv` missing / `Kiev` present → `Kiev`; both missing → error.
- P7: the image must include tzdata (chiseled `-extra` variant or install `tzdata`). Reword "TZ not required" to: the `TZ` env var is not needed, the zone database is.
- P7 manual check: container log shows the correct Kyiv time.

**M11. AC-F1.5 verified in three parts.**
1. Unit test of the WTelegramClient → `ILogger` bridge: known secret values in input lines are replaced with `***`.
2. Host start in run mode with known fake secrets in config and `FakeUpdateSource`; captured logs contain no secret. Tests touching the static `Helpers.Log` are non-parallel.
3. Manual (P1a): run `login`, search the log for phone and code → none.
Spec: reword AC-F1.5 to "automated filter test + automated run-mode start test; login verified manually".

---

## Minor

**m1. Dispatch channel is a wake-up signal only (P4b).** Capacity-1 channel; on signal (and at startup) the dispatcher reads `Pending ORDER BY Id` from the DB, re-reads the status before each send and skips rows no longer `Pending`. Fix plan §3 / P4 wording ("channel of notification ids").

**m2. Retention and foreign keys (P3a).**
- In the migration (DB level, because `ExecuteDelete` bypasses the change tracker): `Notifications.MessageId` ON DELETE CASCADE; `Messages.DuplicateOfMessageId` ON DELETE SET NULL (`IsDuplicate` stays true).
- Boundary test: original older than 30 days, its duplicate younger → original and its notification deleted, duplicate kept with null reference, no error.
- Close Q1: `MetadataRetentionDays` default stays 30; the operator lowers it via config if needed.
- Runbook (P7) section "Reset the database": stop → delete `data/skywatch.db*` (`.db`, `-wal`, `-shm`) → start (migrations recreate it). Do **not** delete the session file or the update state file.

**m3. Options validation and DB use per run mode.**

| Mode | Validated | Database |
|---|---|---|
| `login` | `ApiId`, `ApiHash`, `SessionKey` (session file may not exist yet) | not touched |
| `list-chats` | same + session file exists | not touched |
| `rescan` | `Monitoring:Patterns`, `Storage` | read-only, no migrations |
| `run` | everything (spec §8) | migrations + WAL |

Fix the garbled P1 test line: "`login` does not require the session file to already exist on disk".

**m4. P3a creates the full spec §7 schema in the initial migration**, including `Notifications.Kind`, label columns, all statuses, plus new `Messages.DuplicateReason` (`Forward` / `SameText`, for M7). Add `DuplicateReason` to spec §7. Remove "migration if needed" from P5; P5 and P6 add no migrations (a needed one is a deviation).

**m5. Truncation does not split surrogate pairs (P4a).** If the last kept char is a high surrogate, step back one char. `[Theory]` row: emoji exactly at the cut point → valid string, length ≤ 4096, ends with `…`. Splitting a multi-codepoint flag is accepted.

**m6. Docker (P7).** Dockerfile creates `/app/data` owned by the `app` user (named volume inherits it). Keep `restart: unless-stopped` (Docker backs off up to 1 min). Runbook: on `Critical … run login`, stop the container, run `login`, start again. Manual check: fresh volume → container starts, DB and state file created.

**m7. Test runner: Microsoft.Testing.Platform (P0).** `global.json`: `"test": { "runner": "Microsoft.Testing.Platform" }`. Verify the exact xUnit v3 package set for MTP against current xUnit docs in P0; record it in §5 Dependencies.

**m8. Clock-skew check is passive (P1b).**
- Hourly: take the minimum of `ReceivedAt − PublishedAt` over live messages of the last hour (exclude messages with difference > 120 s, i.e. catch-up). `Warning` if the minimum > 10 s or any value < −2 s; no data → `Debug` and skip.
- No `Help_GetConfig` call (no extra requests from the user account). NTP on the VPS (already in the pre-run checklist) covers startup.
- Pure Core function `ClockSkewEstimator.Evaluate(IEnumerable<(DateTime publishedUtc, DateTime receivedUtc)>)`, `[Theory]` tests with latency sets.
- Spec: rewrite F3.8 accordingly.

**m9. Label callbacks accepted only from the target chat (P6a).** Require `callback.Message.Chat.Id == TargetChatId` **and** user in `LabelerUserIds`. Otherwise answer `Немає прав` and log `Warning` with the chat id. Test: authorized user, foreign chat → no label stored.

**m10. Channel ownership and hosted services (P1b).**
- The bounded channel lives inside `WTelegramUpdateSource`; the update handler writes to it, `ReadAllAsync` reads from it.
- `TrySaveStateAsync` saves only when the channel is empty, otherwise returns `false`.
- Remove `ListenerService`. A single hosted `ProcessingService`: `ResolveSourcesAsync` → read loop → after each event, if the interval elapsed, `TrySaveStateAsync` (single consumer, so nothing is in flight) → on shutdown drain, then `TrySaveStateAsync`.
- Update the P1 file list and the plan structure tree.

**m11. Channel back-pressure (P1b).** Keep full mode = wait. Log `Warning` with the current size when the channel is > 80% full, at most once per minute. Risk table: "long processing stall → channel full → spurious reconnects; mitigated by the 80% warning, WAL + busy timeout". Test: fill to 801 → one `Warning`.

**m12. `ReplyOnChange` debounce in memory: risk accepted.** Add to the risk table: a restart within the 15 s debounce window drops that reply; `ReplyOnChange` is not the default mode.

---

## Answers to the reviewer's questions

| # | Question | Answer |
|---|---|---|
| 1 | Logs: CLAUDE.md vs spec F2.3/F2.4/F6.7 | B2 |
| 2 | Wiring files in every phase | B1 |
| 3 | Edit replies via outbox? | Yes, M5 |
| 4 | Per-pattern precision with several matched patterns | The label counts toward **every** matched pattern. The report notes that one message can count in several patterns. |
| 5 | Re-scan command and retention | M6 |
| 6 | Identical text within 60 min | M7 |
