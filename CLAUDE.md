# CLAUDE.md — Skywatch

Experimental .NET 10 Worker that reads allowlisted Telegram channels/groups (WTelegramClient, user account), matches mentions of Krasnopavlivka (UA/RU), dedupes, and posts unique matches via a bot (Telegram.Bot) to a **private test group** for evaluation.

- Spec (source of truth, AC ids): `docs/spec.md`
- Plan and phases: `docs/plans/feature-telegram-local-alert-aggregator-plan.md`
- Original idea (superseded by the spec): `docs/source/`

## Hard constraints

- Detect mentions only. Never add coordinates, trajectories, movement mapping or military-position logic (spec C1).
- Send only to `Notifications:TargetChatId`. No public targets. Every match message carries the disclaimer (C2, C3).
- Never **commit** secrets or identifying values: api id/hash, phone, 2FA, session key/file, bot token, chat ids, `Sources`, `LabelerUserIds`, `data/`. Use User Secrets locally and env vars on the VPS. The repo is public (C4). Tests and CI use only fake ids, so public CI output reveals nothing.
- Never **log** secrets: api hash, phone, auth code, 2FA, session key/file, bot token (C5). Source names, chat ids and labeler ids are allowed in operator logs (local / VPS, not git).
- `Skywatch.Core` has no package references and does no IO (C7). Pure logic goes there: normalizer, filter, hashing, formatting, time rules.
- Implement one phase per session, stay inside that phase's file list (plus the shared wiring files in plan §8), and stop on any plan conflict (deviation policy in the plan).

## Stack and structure

- .NET 10 / C# latest, `Skywatch.slnx`, Central Package Management (`Directory.Packages.props`), `TreatWarningsAsErrors`, nullable on.
- `src/Skywatch.Core`, `src/Skywatch.Worker` (Options/, Telegram/, Persistence/, Pipeline/, Hosting/), `tests/Skywatch.Core.Tests`, `tests/Skywatch.Worker.Tests`.
- EF Core + SQLite with migrations (no `EnsureCreated`). Built-in DI, Options pattern with `ValidateOnStart`, `BackgroundService`, `ILogger`.

## Conventions

- **Time:** read it only through `TimeProvider`. Persist UTC `DateTime` (converter enforces `Kind=Utc`). Do not use `DateTimeOffset` in entities: SQLite can't translate comparisons on it. Display and day boundaries use `Europe/Kyiv`.
- **Database:** use `IDbContextFactory<SkywatchDbContext>` and a new context per unit of work (one message, one send attempt, one retention batch, one button press). Never inject `DbContext` into singleton services.
- **Telegram seams:** only `ITelegramUpdateSource` and `INotificationSender`. TL types never leave `Worker/Telegram/`; map them in `TlMapper`.
- **Delivery:** outbox pattern. Create the `Pending` notification in the same transaction as the message, then a single dispatcher sends it. Recheck staleness before every send.
- **Abstractions:** don't add interfaces for pure classes, generic repositories, MediatR, Polly, AutoMapper or Serilog.
- **Async:** every async method takes a `CancellationToken` and passes it on.
- **Bot messages:** plain text, no parse mode. Notification and label strings are Ukrainian.

## Tests

- xUnit v3 + Shouldly. Name each test after the spec AC it proves: `AC_F5_3_abbreviation_after_preposition_matches`.
- Derive tests from the spec, not from the implementation. Each new false positive or missed form becomes a `[Theory]` row.
- EF tests use SQLite in-memory (open connection), never the EF InMemory provider, because unique indexes matter.
- Use `FakeTimeProvider` instead of `Task.Delay`. Use hand-written fakes in `tests/Skywatch.Worker.Tests/Fakes/`, not mocking libraries.

## Commands

```powershell
dotnet build                                              # build
dotnet test                                               # all tests
dotnet run --project src/Skywatch.Worker -- login         # interactive first login (writes data/session.dat)
dotnet run --project src/Skywatch.Worker -- list-chats    # print chat ids for Monitoring:Sources
dotnet run --project src/Skywatch.Worker                  # run
dotnet ef migrations add <Name> --project src/Skywatch.Worker
```

## Verification

`/verify` (`build/verify.ps1`) runs restore → build (warnings as errors) → test → `dotnet format --verify-no-changes` → gitleaks. It must be green before every commit. CI (`.github/workflows/ci.yml`) runs the same steps.

- Run: `powershell -NoProfile -File build/verify.ps1` (Windows PowerShell 5.1) or `pwsh build/verify.ps1` (CI). Stops at the first failing stage with exit code 1.
- gitleaks (v8.30, `winget install gitleaks`) runs twice: `gitleaks git --staged` and `gitleaks git` (full history), both with `.gitleaks.toml`. Custom rules: bare Telegram bot token, `-100` + 10-digit channel id. Fake ids for tests: `-1000000000NNN` (allowlisted).
- `build/install-hooks.ps1` installs the gitleaks pre-commit hook; run it once per clone.
- `dotnet test` uses Microsoft.Testing.Platform (`global.json`); zero tests run is a failure (exit code 8).
- `nuget.config` clears machine package sources (CPM fails on multiple sources), keep nuget.org only.

If gitleaks finds anything, stop immediately: don't commit, don't "fix forward", report it.
