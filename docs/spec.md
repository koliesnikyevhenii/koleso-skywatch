# Skywatch — Product Specification

| | |
|---|---|
| Status | Draft v1 — agreed 2026-10-26 |
| Owner | Yevhenii Koliesnik (.NET lead) |
| Source idea | [docs/source/telegram_local_alert_aggregator_feature_plan.md](source/telegram_local_alert_aggregator_feature_plan.md) |
| Plan | [docs/plans/feature-telegram-local-alert-aggregator-plan.md](plans/feature-telegram-local-alert-aggregator-plan.md) |

This document is the source of truth. Where it conflicts with the source idea document, this document wins. Tests reference acceptance criteria by ID (`AC-F5.3`).

---

## 1. Purpose

Skywatch is an **experimental** .NET 10 Worker Service that listens to an allowlist of Telegram channels and groups, detects mentions of the village of **Krasnopavlivka** (Kharkiv oblast, Lozova district) in Ukrainian and Russian text, removes duplicates, and posts unique matches to a **private test group** via a Telegram bot.

The MVP exists to **measure**: filter precision, match volume, duplicate frequency, notification noise and end-to-end latency over a 3–7 day run. A decision on any wider use is made only after that.

## 2. Hard constraints

These are non-negotiable. Violating one is a defect regardless of what a phase says.

- **C1** — The system detects *mentions* of configured names. It MUST NOT infer coordinates, predict flight paths, track military activity or air-defence positions, or map movement reports.
- **C2** — Notifications go only to the single configured private test group. There is no public target in this release.
- **C3** — Every match notification carries a disclaimer that the content is unverified third-party information and is not a substitute for official air-raid alerts.
- **C4** — No secrets or identifying configuration in git: API id/hash, phone, 2FA password, bot token, chat ids, source list, labeler user ids, session file, database.
- **C5** — Secrets, auth codes, session data and API hashes are never logged.
- **C6** — No LLM, OCR, transcription or paid external API.
- **C7** — `Skywatch.Core` has no package reference to Telegram libraries or EF Core.

## 3. Glossary

| Term | Meaning |
|---|---|
| Source | An allowlisted Telegram channel, supergroup or basic group the reader account is a member of |
| Reader account | The Telegram *user* account Skywatch logs in as over MTProto (WTelegramClient) to read sources |
| Bot | The Telegram bot (Telegram.Bot) that posts into the test group |
| Test group | The private Telegram group receiving notifications |
| Labeler | A Telegram user allowed to press label buttons |
| Pattern | A named, configurable regular expression that defines a match |
| Origin key | `(chatId, messageId)` of the original post — the forward source if the message is a forward, else the message itself |
| Age | `now − (EditedAt ?? PublishedAt)` of the event being processed |
| Stale | Age greater than `Notifications:MaxMessageAgeSeconds` |

## 4. Accounts and run modes

- **Development:** the owner's personal Telegram account as the reader account, on the owner's Windows machine.
- **Experiment:** a separate dedicated reader account (own phone number, warmed up manually), running on a Linux VPS in Docker.
- The bot is created via @BotFather and added to the test group.

---

## 5. Functional requirements

### F1. Authentication and run modes

The Worker executable supports four modes selected by the first argument.

- **AC-F1.1** — `login` runs an interactive console flow: it reads `ApiId`, `ApiHash`, `PhoneNumber` from configuration, prompts for the verification code and, if required, the 2FA password, writes an encrypted session file to `Telegram:SessionPath`, prints the logged-in user's display name and exits with code 0.
- **AC-F1.2** — `list-chats` uses the existing session and prints every chat the reader account belongs to: Bot-API-style marked chat id (e.g. `-100…` for channels), type (`Channel`, `Supergroup`, `Group`), title, public username (if any) and the `noforwards` flag. It then exits with code 0.
- **AC-F1.3** — With no argument the service runs normally and never prompts for input. If the session file is missing or rejected, it logs a `Critical` message telling the operator to run `login`, and exits with a non-zero code.
- **AC-F1.4** — The session file is encrypted with `Telegram:SessionKey`. Session path and key come from configuration; neither is committed.
- **AC-F1.5** — Log output in all modes contains none of: API hash, phone number, verification code, 2FA password, session key, bot token (verified by an automated log-filter test and an automated run-mode start test with known secret values; the `login` flow is verified manually).
- **AC-F1.6** — `rescan [--pattern "<regex>"]` reads stored unmatched messages that still have text, runs the configured patterns (or the one-off regex), prints date, source, message id, matching pattern and a text snippet for each hit, and exits with code 0. It is read-only: it sends nothing, writes nothing and applies no migrations.

### F2. Source management

- **AC-F2.1** — Sources are configured in `Monitoring:Sources` as `{ ChatId, Name, Enabled }`, using the Bot-API-style marked chat ids that `list-chats` prints.
- **AC-F2.2** — On startup each enabled source is resolved against the reader account's chats. Resolved sources are upserted into the `Sources` table with type, username and `noforwards` flag. Configuration is the source of truth for `Enabled`.
- **AC-F2.3** — An enabled source that cannot be resolved produces a `Warning` naming the configured `Name` and `ChatId`. It is skipped; startup continues.
- **AC-F2.4** — At startup, each source with `noforwards = true` is logged at `Information`.
- **AC-F2.5** — Updates from chats that are not enabled sources are discarded before normalization and are not persisted.

### F3. Real-time listener

- **AC-F3.1** — New messages are received through MTProto update events (WTelegramClient `UpdateManager`), not by polling history.
- **AC-F3.2** — On first start (no update-state file) no historical messages are imported.
- **AC-F3.3** — After a reconnect or restart with a saved update state, missed updates are caught up. Caught-up messages flow through the normal pipeline and are subject to staleness (F7).
- **AC-F3.4** — The update handler only maps and enqueues: it writes an `IncomingMessage` into a bounded channel (capacity 1000, full mode = wait) and returns. Processing happens in a separate consumer.
- **AC-F3.5** — The text used for a message is its text, or the media caption for media messages. Messages with neither are discarded without persistence.
- **AC-F3.6** — For forwarded messages, the forward origin `(chatId, messageId)` is captured when Telegram provides it.
- **AC-F3.7** — Disconnects and reconnects are logged (without secrets) and recorded as `ServiceEvents` (from P3 on).
- **AC-F3.8** — Hourly the service estimates clock skew passively from live messages of the last hour: the minimum of `ReceivedAt − PublishedAt`, excluding differences over 120 s (catch-up). It logs a `Warning` if the minimum exceeds 10 seconds or any value is below −2 seconds; with no data it logs at `Debug` and skips. No extra Telegram requests are made.
- **AC-F3.9** — Edited messages (`UpdateEditChannelMessage`, `UpdateEditMessage`) are mapped to `IncomingMessage` with `IsEdit = true` and `EditedAtUtc` set, and handled per F9.

### F4. Text normalization

`TextNormalizer.Normalize(string)` returns `NormalizedText(string Lower, string CasePreserved)`. Both variants get the same cleanup; `Lower` is additionally lowercased.

- **AC-F4.1** — Unicode is normalized to NFKC.
- **AC-F4.2** — Zero-width characters (U+200B–U+200D, U+2060, U+FEFF) and soft hyphen (U+00AD) are removed.
- **AC-F4.3** — Runs of whitespace (including newlines and NBSP) collapse to a single space; the result is trimmed.
- **AC-F4.4** — `ё`/`Ё` become `е`/`Е`.
- **AC-F4.5** — `Lower` uses invariant lowercasing; Cyrillic `І`→`і`, `Ї`→`ї` are lowercased correctly.
- **AC-F4.6** — `і` and `и` are NOT merged; Latin look-alike characters are NOT converted (decision D10).
- **AC-F4.7** — Null or whitespace-only input returns empty strings without throwing.

### F5. Keyword filtering

`KeywordFilter.Match(NormalizedText)` returns `FilterResult(bool IsMatch, IReadOnlyList<PatternHit> Hits)`, where `PatternHit(string PatternName, string MatchedText)`.

Patterns are configured in `Monitoring:Patterns` as `{ Name, Regex, CaseSensitive, Enabled }`. Case-insensitive patterns run against `Lower`, case-sensitive ones against `CasePreserved`.

Default patterns:

| Name | Regex | CaseSensitive | Intent |
|---|---|---|---|
| `Settlement` | `\bкрасно[-\s]?павл(?:івк\|івц\|овк)\w*` | false | All case forms, UA and RU, including locative `Краснопавлівці` / `Краснопавловке` |
| `Adjective` | `\bкрасно[-\s]?павл(?:івськ\|овск)\w*` | false | `Краснопавлівське водосховище`, `краснопавловский` |
| `Abbreviation` | `\b(?i:на\|в\|у\|над\|по\|біля\|возле\|до\|через\|під\|под)\s+К[Пп]\b` | true | `на КП`, `над КП`; not `КП "Водоканал"` |
| `Colloquial` | `\bкраснопе\b` | false | Local name; does not decline |

- **AC-F5.1** — Every form in this list matches `Settlement`: Краснопавлівка, Краснопавлівки, Краснопавлівці, Краснопавлівку, Краснопавлівкою, Краснопавлівко, Краснопавловка, Краснопавловки, Краснопавловке, Краснопавловку, Краснопавловкой, Краснопавловкою, Красно-Павлівка, `#Краснопавлівка`, and each of these in upper case and in mixed case.
- **AC-F5.2** — `Краснопавлівське`, `краснопавлівського`, `Краснопавловское` match `Adjective` and do not match `Settlement`.
- **AC-F5.3** — `на КП`, `Над КП`, `курсом на КП`, `біля Кп`, `2 БпЛА в КП` match `Abbreviation`.
- **AC-F5.4** — `КП "Харківводоканал"`, `уражено КП`, `по даним КП`, `на кп` (lower case) do not match `Abbreviation`.
- **AC-F5.5** — `на Краснопе`, `Краснопе.`, `(Краснопе)` match `Colloquial`. `Красноперекопськ`, `красноперка`, `краснопёрка` match no pattern.
- **AC-F5.6** — Patterns never match inside another word (e.g. `якраснопавлівка` does not match).
- **AC-F5.7** — When several patterns match, all hits are returned in configuration order.
- **AC-F5.8** — Disabled patterns are not evaluated.
- **AC-F5.9** — Patterns are compiled at startup with `RegexOptions.CultureInvariant` and a 100 ms match timeout (`RegexOptions.IgnoreCase` added when `CaseSensitive = false`). An invalid regex, empty `Name`, duplicate `Name` or zero enabled patterns fails startup via options validation.
- **AC-F5.10** — A regex timeout during matching is logged at `Warning` with the pattern name and treated as "no match" for that pattern; processing continues.
- **AC-F5.11** — Matching an empty normalized text returns `IsMatch = false`.

### F6. Persistence, idempotency and duplicate detection

- **AC-F6.1** — Every allowlisted message with text is stored as a `Messages` row with metadata (source, Telegram message id, published/received/edited times, content hash, origin key, match result, matched pattern names). Text is stored when the message matched, or when `Storage:StoreUnmatchedText = true`.
- **AC-F6.2** — `(SourceId, TelegramMessageId)` is unique (database index). A new-message event for an existing key creates no row and no notification.
- **AC-F6.3** — `ContentHash` is the lowercase hex SHA-256 of the UTF-8 bytes of `NormalizedText.Lower`.
- **AC-F6.4** — A matched message is a **duplicate** if an earlier matched, non-duplicate message exists with (a) the same origin key, received within `Monitoring:DuplicateWindowMinutes` — reason `Forward`; or (b) the same content hash, received within `Monitoring:ContentDuplicateWindowMinutes` and, when `Monitoring:ContentDuplicateAcrossSourcesOnly = true`, from a different source — reason `SameText`. Origin key is checked first. The reason is stored in `DuplicateReason`.
- **AC-F6.5** — A duplicate is stored with `IsDuplicate = true` and `DuplicateOfMessageId` set, and gets a `Notification` with status `SkippedDuplicate` (it is never sent).
- **AC-F6.6** — A matched, non-duplicate message gets a `Notification` with status `Pending` in the same database transaction as the message row, when `Notifications:Enabled = true`.
- **AC-F6.7** — When `Notifications:Enabled = false`, no `Notification` rows are created; the match is logged at `Information` with source name and pattern names.
- **AC-F6.8** — Processing an exception-throwing message logs an `Error`, records a `ProcessingError` service event and continues with the next message.

### F7. Staleness

- **AC-F7.1** — A `Pending` notification whose message is stale is set to `SkippedStale` instead of being sent. Staleness is checked when the notification is created, before every send attempt, and when recovering `Pending` rows at startup.
- **AC-F7.2** — Default `Notifications:MaxMessageAgeSeconds` = 120.
- **AC-F7.3** — For edit-triggered notifications, age is measured from `EditedAtUtc`.
- **AC-F7.4** — Every 30 seconds, if there are `SkippedStale` notifications that have not been reported, the bot sends one summary to the test group: `⚠️ Пропущено {N} застарілих збігів (публікації {HH:mm}–{HH:mm})`, and marks them reported. The summary itself is never stale-checked.

### F8. Notification delivery

- **AC-F8.1** — A single consumer sends `Pending` notifications sequentially, in creation order.
- **AC-F8.2** — Message format (plain text, no parse mode):

  ```text
  ⚠️ Неперевірено · повідомлення з каналу третьої сторони. Не замінює офіційні сигнали тривоги.
  🏷 {PatternNames, comma-separated}{ · ✏️ оновлено, if edit-triggered}

  {original text}

  ──────────
  📢 {Source name}
  🕐 {published time HH:mm:ss, display time zone} (опубл.) · затримка {N} с
  🔗 {link, if available}
  ```
- **AC-F8.3** — Link rules: public channel or supergroup with username → `https://t.me/{username}/{messageId}`; private channel or supergroup → `https://t.me/c/{internalId}/{messageId}` (internal id = marked id without the `-100` prefix); basic group → no link line.
- **AC-F8.4** — If the formatted message would exceed 4096 characters, the original text is truncated with `…` so the whole message fits; header and footer are never truncated.
- **AC-F8.5** — `{N}` is `round(SentAttemptAt − PublishedAt)` in whole seconds (or from `EditedAt` for edits).
- **AC-F8.6** — On HTTP 429 the sender waits `retry_after` seconds and retries; this does not count against the attempt limit.
- **AC-F8.7** — On network errors, timeouts and 5xx it retries up to `Notifications:MaxSendAttempts` (default 3) total attempts with backoff 1 s, 2 s, 4 s. After the last failure the status is `Failed` with the error message stored.
- **AC-F8.8** — On 4xx other than 429 the status is immediately `Failed` and a `Critical` log is written.
- **AC-F8.9** — On a timeout where delivery is unknown, the retry proceeds and the notification is flagged `PossibleDuplicate = true`.
- **AC-F8.10** — On success the status is `Sent` and `SentAtUtc` and `BotMessageId` are stored.
- **AC-F8.11** — At startup all `Pending` notifications are re-queued in creation order, after the staleness check.
- **AC-F8.12** — Times are shown in `Notifications:DisplayTimeZone` (default `Europe/Kyiv`), honouring DST.
- **AC-F8.13** — `protected-content` (`noforwards`) sources get the same full-text format as others (decision D4).

### F9. Edited messages

`Monitoring:EditHandling` = `Ignore | NotifyOnNewMatch | ReplyOnChange` (default `NotifyOnNewMatch`).

- **AC-F9.1** — `Ignore`: edit events do not change stored data and create no notifications.
- **AC-F9.2** — In all non-`Ignore` modes, an edit updates the stored message's `EditedAtUtc`, `ContentHash`, match result and text (subject to F6.1 text-storage rules).
- **AC-F9.3** — `NotifyOnNewMatch`: if the message previously did not match (or is unknown) and now matches, a notification is created with the `✏️ оновлено` marker, subject to duplicate detection (F6.4) and staleness (F7.3).
- **AC-F9.4** — `NotifyOnNewMatch`: an edit to an already-notified message creates no notification.
- **AC-F9.5** — `ReplyOnChange`: behaves as `NotifyOnNewMatch`, and additionally, when an already-`Sent` message's content hash changes, a reply to the original `BotMessageId` with the new formatted text is sent once no further edits arrive for `Monitoring:EditDebounceSeconds` (default 15).
- **AC-F9.6** — Edits whose content hash equals the stored hash are ignored in every mode.

### F10. Labeling

- **AC-F10.1** — Every sent match notification (not summaries or reports) carries inline buttons `✅ Корисно`, `❌ Хибне`, `🔁 Дубль`.
- **AC-F10.2** — A press by a user in `Notifications:LabelerUserIds` stores `Label`, `LabeledByUserId`, `LabeledAtUtc` on the notification, answers the callback, and edits the buttons to mark the chosen one with `✔`.
- **AC-F10.3** — Pressing a different button overwrites the label; the latest label wins.
- **AC-F10.4** — A press by any other user changes nothing and answers the callback with `Немає прав`.
- **AC-F10.5** — Callback data is `lbl:{notificationId}:{u|f|d}`. Malformed data or an unknown notification id is answered and logged at `Warning`, without throwing.
- **AC-F10.6** — Bot callbacks are received via long polling in a hosted service in the same Worker process.

### F11. Daily report and metrics

- **AC-F11.1** — When `Reporting:DailyReportEnabled` and `Notifications:Enabled` are both true, at `Reporting:DailyReportLocalTime` (default `09:00`, display time zone) the bot posts a report for the previous local calendar day (00:00–24:00 in the display time zone; 23 or 25 hours on DST change days).
- **AC-F11.2** — The report contains: messages received; matches (total and per pattern); duplicates (forwards / same text); unique matches; notifications sent / skipped-stale / failed; labels useful / false / duplicate / unlabeled; precision (useful ÷ labeled) overall and per pattern (a message that matched several patterns counts toward each of them, and the report says so); latency p50 and p95 of `ReceivedAt − PublishedAt` and `SentAt − ReceivedAt`; reconnect count and total disconnected time; processing errors.
- **AC-F11.3** — If reporting is enabled but notifications are disabled, the report is written to the log at `Information` instead.
- **AC-F11.4** — A missed report time (service down) is sent once on the next start if the previous day's report was not sent; reports are not sent twice for the same day.
- **AC-F11.5** — Precision with zero labeled items is shown as `n/a`, not as a division error.

### F12. Retention

A hosted service runs hourly.

- **AC-F12.1** — Text of unmatched messages older than `Storage:UnmatchedRetentionDays` (default 3) is set to null.
- **AC-F12.2** — Text of matched messages older than `Storage:RetentionDays` (default 7) is set to null.
- **AC-F12.3** — `Messages`, `Notifications` and `ServiceEvents` rows older than `Storage:MetadataRetentionDays` (default 30) are deleted.
- **AC-F12.4** — Age for retention is measured from `ReceivedAtUtc` (or `OccurredAtUtc` for service events).

---

## 6. Configuration reference

Committed `appsettings.json` contains only non-identifying defaults. Identifying values come from User Secrets (development) or environment variables / an uncommitted file (VPS). `appsettings.Example.json` shows the full shape with fictional values.

```jsonc
{
  "Telegram": {
    "ApiId": 0,                 // secret
    "ApiHash": "",              // secret
    "PhoneNumber": "",          // secret
    "SessionPath": "data/session.dat",
    "SessionKey": "",           // secret
    "UpdateStatePath": "data/updates.state",
    "UpdateStateSaveIntervalSeconds": 60
  },
  "Monitoring": {
    "Enabled": true,
    "DuplicateWindowMinutes": 60,           // origin-key (forward) duplicates
    "ContentDuplicateWindowMinutes": 10,    // identical text
    "ContentDuplicateAcrossSourcesOnly": true,
    "EditHandling": "NotifyOnNewMatch",
    "EditDebounceSeconds": 15,
    "Patterns": [ /* defaults from F5 */ ],
    "Sources": []               // identifying — not committed
  },
  "Notifications": {
    "Enabled": false,
    "BotToken": "",             // secret
    "TargetChatId": 0,          // identifying
    "LabelerUserIds": [],       // identifying
    "MaxMessageAgeSeconds": 120,
    "MaxSendAttempts": 3,
    "DisplayTimeZone": "Europe/Kyiv"
  },
  "Reporting": {
    "DailyReportEnabled": true,
    "DailyReportLocalTime": "09:00"
  },
  "Storage": {
    "DatabasePath": "data/skywatch.db",
    "StoreUnmatchedText": true,
    "RetentionDays": 7,
    "UnmatchedRetentionDays": 3,
    "MetadataRetentionDays": 30,
    "BusyTimeoutSeconds": 30,
    "RetentionBatchSize": 500
  }
}
```

All options classes use `ValidateDataAnnotations` / custom validators and `ValidateOnStart`. In normal run mode, startup fails if `Telegram:ApiId`, `ApiHash` or `SessionKey` is missing, or if `Notifications:Enabled = true` and `BotToken` or `TargetChatId` is missing.

## 7. Data model (SQLite, EF Core)

All timestamps are UTC `DateTime` stored with a converter that guarantees `DateTimeKind.Utc` on read.

**Sources** — `Id`, `TelegramChatId` (unique), `Name`, `Type` (Channel/Supergroup/Group), `Username?`, `NoForwards`, `IsEnabled`, `CreatedAtUtc`.

**Messages** — `Id`, `SourceId`, `TelegramMessageId`, `Text?`, `PublishedAtUtc`, `ReceivedAtUtc`, `EditedAtUtc?`, `ContentHash`, `OriginChatId`, `OriginMessageId`, `IsMatched`, `MatchedPatterns?` (comma-separated names), `IsDuplicate`, `DuplicateOfMessageId?`, `DuplicateReason?` (Forward/SameText).
Indexes: unique `(SourceId, TelegramMessageId)`; `(ContentHash, ReceivedAtUtc)`; `(OriginChatId, OriginMessageId, ReceivedAtUtc)`; `ReceivedAtUtc`.

**Notifications** — `Id`, `MessageId`, `Kind` (New/EditNewMatch/EditReply), `Status` (Pending/Sent/SkippedStale/SkippedDuplicate/Failed), `Attempts`, `CreatedAtUtc`, `SentAtUtc?`, `BotMessageId?`, `Error?`, `PossibleDuplicate`, `StaleReportedAtUtc?`, `Label?` (Useful/False/Duplicate), `LabeledByUserId?`, `LabeledAtUtc?`.

**ServiceEvents** — `Id`, `Kind` (Started/Stopped/Disconnected/Reconnected/ProcessingError/DailyReportSent), `OccurredAtUtc`, `Detail?`.

Schema changes go through EF Core migrations, applied at startup.

## 8. Non-functional requirements

- **N1 — Latency:** in steady state, p95 of `SentAt − PublishedAt` ≤ 5 s, as measured by the daily report. (A target to measure, not a gate.)
- **N2 — Resource use:** idle memory under ~150 MB, negligible CPU.
- **N3 — Async correctness:** every async API accepts and propagates `CancellationToken`; no sync-over-async.
- **N4 — Graceful shutdown:** on stop, the listener stops reading, the processing channel drains within the host shutdown timeout, and unsent notifications stay `Pending` for the next start.
- **N5 — Time:** all code reads time through `TimeProvider`.
- **N6 — Logging:** `ILogger` structured logging; JSON console formatter in containers.
- **N7 — Platforms:** runs on Windows (console or Windows Service) and Linux (Docker).
- **N8 — Testability:** the pipeline is testable end-to-end without network via `ITelegramUpdateSource` and `INotificationSender` fakes, SQLite in-memory and `FakeTimeProvider`.

## 9. Out of scope (this release)

Web dashboard; mobile app; HTTP API; LLM classification; OCR; voice transcription; geolocation, trajectories, maps; public broadcasting; Latin transliteration and look-alike-character handling; neighbouring villages; disambiguation of same-name places; similarity (fuzzy) dedup; batching several matches into one message; `/missed` bot command; automatic deploy from CI; MediatR, Polly, generic repositories, AutoMapper, Redis, message brokers.

## 10. Success metrics (evaluated after a 3–7 day run)

1. Total messages processed.
2. Matches, per pattern.
3. Precision (labeled useful ÷ labeled), overall and per pattern.
4. Duplicates removed.
5. Notifications per hour and per day.
6. Important mentions missed (found with the `rescan` run mode (AC-F1.6) over stored unmatched text and by manual review).
7. Connection stability: reconnects, total downtime, stale-skipped count.
8. Latency p50/p95, split into Telegram delivery and own processing.

## 11. Decision log

| # | Decision |
|---|---|
| D1 | Unmatched-message text storage is switchable (`StoreUnmatchedText`, default on during tuning) with separate short retention; metadata is always stored. |
| D2 | Stale cutoff 120 s (from publish or edit time); stale matches are stored but not sent; one summary message per batch. Latency target p95 ≤ 5 s, measured. |
| D3 | Edit handling is a configurable mode: `Ignore`, `NotifyOnNewMatch` (default), `ReplyOnChange` with 15 s debounce. |
| D4 | Full text is sent for all sources including `noforwards` ones (private test group); revisit before any wider audience. |
| D5 | Labeling via inline buttons, restricted to `LabelerUserIds`; daily precision computed automatically. |
| D6 | Development and first login on Windows; experiment on a Linux VPS in Docker, deployed before the run starts. |
| D7 | Tests: xUnit v3, Shouldly, SQLite in-memory (not EF InMemory), `FakeTimeProvider`, hand-written fakes. |
| D8 | `/verify` = restore, build (warnings as errors), test, `dotnet format --verify-no-changes`, gitleaks; same steps in GitHub Actions; `global.json` pins SDK 10.0.401 with `latestFeature`; GitHub secret scanning + push protection enabled; no NuGet lock files; no CI deploy. |
| D9 | Store UTC `DateTime` with converter; display and day boundaries in `Europe/Kyiv`; daily report at 09:00 local. |
| D10 | Named patterns `Settlement`, `Adjective`, `Abbreviation` (КП after a preposition, case-sensitive), `Colloquial` (`Краснопе`, whole word). No Latin transliteration, no look-alike folding, no neighbouring villages in the MVP. |
| D11 | Outbox delivery: Pending row → channel → single sender; 429 honoured; 3 attempts with 1/2/4 s backoff; staleness rechecked per attempt; no Polly; at-least-once with `PossibleDuplicate` flag. |
| D12 | Projects `Skywatch.Core`, `Skywatch.Worker`, `Skywatch.Core.Tests`, `Skywatch.Worker.Tests`; solution `Skywatch.slnx`; `Dockerfile` at repo root. |
| D13 | Only two abstractions over Telegram: `ITelegramUpdateSource`, `INotificationSender`; bounded `Channel<IncomingMessage>(1000)`. |
| D14 | Personal account for local development; separate warmed-up account for the VPS experiment. Run modes `login` and `list-chats`. |
