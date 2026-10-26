# Feature Plan: Telegram Local Alert Aggregator

## 1. Project Overview

**Project name:** Telegram Local Alert Aggregator  
**Technology:** .NET 10, C#, WTelegramClient, Telegram.Bot, SQLite  
**Application type:** .NET Worker Service  
**Deployment:** Windows initially; Linux/Docker later  
**Development workflow:** Claude Code

### Goal

Build an experimental Telegram aggregator that receives new messages from a configured set of Telegram channels and groups, filters mentions of Krasnopavlivka (Краснопавлівка / Краснопавловка), and produces notifications in a private test Telegram group.

The primary objective of the MVP is to measure filtering accuracy, match volume, duplicate frequency, and notification noise before deciding whether the system is useful to residents.

The application must not infer drone coordinates, predict flight paths, track military activity, or replace official air-raid warning systems.

## 2. High-Level Architecture

```text
Telegram Channels / Groups
          |
          v
  WTelegramClient (MTProto)
          |
          v
    Message Listener
          |
          v
   Source Validation
          |
          v
     Text Normalizer
          |
          v
      Keyword Filter
          |
          v
    Duplicate Detector
          |
          v
       SQLite
          |
          v
   Notification Service
          |
          v
    Telegram Bot API
          |
          v
    Private Test Group
```

During the MVP, notifications are delivered only to a private test group. Automatic public distribution of unverified real-time drone-movement reports is outside the MVP scope.

## 3. Functional Requirements

### F1. Telegram Client Authentication

Use `WTelegramClient` to connect with a Telegram user account.

Requirements:

- Authenticate using `api_id` and `api_hash`.
- Support Telegram two-step verification.
- Persist the authorized session securely.
- Reconnect after transient network failures.
- Store secrets outside source control.
- Never log passwords, authentication codes, API hashes, or session data.

After the first successful login, the application should reuse the valid saved session automatically.

### F2. Source Management

Maintain an allowlist of Telegram sources:

- Public channels.
- Private channels accessible to the account.
- Telegram groups and supergroups accessible to the account.

Store for each source:

- Telegram chat ID.
- Display name.
- Source type.
- Enabled/disabled status.
- Date added.

Configure the initial list in `appsettings.json`:

```json
{
  "Monitoring": {
    "Sources": [
      {
        "ChatId": -1001234567890,
        "Name": "Example Channel",
        "Enabled": true
      }
    ]
  }
}
```

The example chat ID is fictional.

### F3. Real-Time Message Listener

Process new-message events received via MTProto.

Requirements:

- Event-driven processing rather than periodic history polling.
- Process only allowlisted sources.
- Do not import historical messages on the first startup.
- Prevent duplicate processing of the same message.
- Handle disconnections and reconnects gracefully.
- Log connection failures without exposing secrets.

Telegram may deliver updates after a reconnect; distinguish genuinely new events from previously processed ones.

### F4. Text Normalization

Normalize text before filtering:

- Convert to lowercase using culture-aware or invariant rules as appropriate.
- Collapse excess whitespace.
- Normalize Unicode.
- Support Russian and Ukrainian text.
- Include media captions when available.

Recognize configurable variants such as:

- Краснопавловка
- Краснопавловки
- Краснопавловку
- Краснопавлівка
- Краснопавлівки
- Краснопавлівку

### F5. Keyword Filtering

Use deterministic keyword matching and regular expressions in the MVP. Do not integrate an LLM.

Suggested contract:

```csharp
public sealed record FilterResult(
    bool IsMatch,
    string? MatchedKeyword,
    string? MatchReason
);
```

The filter detects a mention of the settlement; it does not determine an actual direction of movement or verify the source's claims.

### F6. Duplicate Detection

Messages may be copied across multiple channels.

Implement two levels:

**Exact message duplicate:** same Telegram message ID within the same source.

**Exact content duplicate:** same normalized text across sources, identified by a SHA-256 content hash.

Persist:

- Source ID.
- Telegram message ID.
- Content hash.
- Received timestamp.
- Duplicate status.

Similarity-based deduplication can be considered later. Exact matching is sufficient for the MVP.

### F7. Telegram Notification Service

Use `Telegram.Bot` to publish matches in a **private test group**.

Requirements:

- Send as the bot.
- Include the original text.
- Include source name.
- Include original publication time.
- Include a source link when available.
- Mark content as unverified.
- Avoid duplicate notifications.
- Handle Telegram rate limits and transient errors.
- Respect protected-content and forwarding restrictions.

Public deployment must be a separate decision after evaluating results and choosing an appropriate safety-focused notification format.

### F8. SQLite Storage

Use SQLite with EF Core.

Suggested entities:

**Sources**

- Id
- TelegramChatId
- Name
- IsEnabled
- CreatedAt

**Messages**

- Id
- SourceId
- TelegramMessageId
- Text
- ReceivedAt
- PublishedAt
- ContentHash
- IsMatched
- IsDuplicate

**Notifications**

- Id
- MessageId
- Status
- SentAt
- Error

Limit raw message retention, with a configurable default of seven days.

### F9. Logging and Observability

Use `ILogger` and structured logging.

Collect:

- Number of messages received.
- Number of keyword matches.
- Number of duplicates.
- Number of notifications sent.
- Error count.
- Average processing time.
- Notifications per hour and per day.

Support a daily statistics summary in the private test group.

Example (illustrative values):

```text
Telegram Aggregator — Daily Report

Messages processed: 1,240
Keyword matches: 28
Duplicates: 11
Unique matches: 17
Notifications sent: 17
Errors: 0
```

## 4. Suggested Solution Structure

```text
TelegramLocalAggregator/
|
|-- src/
|   |-- TelegramLocalAggregator.Worker/
|   |   |-- Program.cs
|   |   |-- appsettings.json
|   |
|   |-- TelegramLocalAggregator.Application/
|   |   |-- Interfaces/
|   |   |-- Services/
|   |   |-- Models/
|   |
|   |-- TelegramLocalAggregator.Infrastructure/
|       |-- Telegram/
|       |-- Persistence/
|       |-- Configuration/
|
|-- tests/
|   |-- TelegramLocalAggregator.Tests/
|
|-- README.md
|-- Dockerfile
|-- .gitignore
```

Use built-in .NET dependency injection, the Options pattern, and `BackgroundService`. Do not introduce MediatR, RabbitMQ, Redis, or a separate HTTP API without a demonstrated need.

## 5. Configuration

Keep settings externalized:

```json
{
  "Monitoring": {
    "Enabled": true,
    "Keywords": [
      "краснопавловка",
      "краснопавлівка"
    ],
    "IgnoreMessageHistory": true,
    "DuplicateWindowMinutes": 60,
    "Sources": []
  },
  "Notifications": {
    "Enabled": false,
    "TestMode": true,
    "DailySummaryEnabled": true
  },
  "Storage": {
    "RetentionDays": 7
  }
}
```

Store Telegram secrets separately using environment variables or .NET User Secrets.

## 6. Development Phases

### Phase 1 — Telegram Connectivity

- Create the .NET Worker project.
- Configure `WTelegramClient`.
- Implement authentication and session persistence.
- Connect to a few test channels.
- Receive new messages.
- Output safe diagnostic information to the console.

**Acceptance criteria:** The Worker receives new messages from allowlisted sources without importing history.

### Phase 2 — Filtering Engine

- Implement `TextNormalizer`.
- Implement `KeywordFilter`.
- Add Russian and Ukrainian spelling variants.
- Add unit tests.
- Test false positives.

**Acceptance criteria:** The filter recognizes configured forms and avoids matching unrelated substrings inside other words.

### Phase 3 — Persistence and Deduplication

- Add EF Core SQLite.
- Persist message metadata.
- Implement per-source message-ID deduplication.
- Implement normalized-content hashing.
- Configure retention and cleanup.

**Acceptance criteria:** Reprocessing a message does not generate a second notification.

### Phase 4 — Private Test Notifications

- Integrate Telegram Bot API.
- Create a private test group.
- Format notification messages.
- Implement retry handling.
- Add notification throttling.

**Acceptance criteria:** Only unique matching messages appear in the private test group.

### Phase 5 — Experiment Analytics

- Collect filtering metrics.
- Calculate hourly and daily notification counts.
- Identify sources generating the most duplicates.
- Generate daily summaries.
- Manually review a sample of false positives.

**Acceptance criteria:** After several days, filtering quality and notification volume can be evaluated quantitatively.

### Phase 6 — Deployment

- Prepare a Dockerfile.
- Configure persistent volumes for SQLite and the Telegram session.
- Add graceful shutdown.
- Configure automatic restarts.
- Document Windows and Linux deployment.

## 7. Non-Functional Requirements

- Low CPU and memory consumption.
- Asynchronous message processing.
- Proper `CancellationToken` propagation.
- Resilience to temporary Telegram outages.
- Idempotent message handling.
- Secure credential storage.
- Unit tests for filtering and deduplication.
- No paid external APIs.
- Support for continuous operation.

## 8. Out of Scope for MVP

Do not implement:

- Web dashboard.
- Mobile application.
- AI/LLM classification.
- OCR for images.
- Voice-message transcription.
- Drone geolocation.
- Flight-path prediction.
- Collection of air-defense positions.
- Automatic mapping of movement reports.
- Public broadcasting of unverified operational reports.

## 9. Success Metrics

After a 3–7-day test period, measure:

1. Total processed messages.
2. Number of messages containing configured keywords.
3. Percentage of matches judged useful.
4. Number of duplicates removed.
5. Notifications per hour.
6. Important mentions missed by the filter.
7. Connection stability and error rate.

## 10. Instructions for Claude Code

Implement the project incrementally, starting with Phase 1.

Requirements:

- Use .NET 10 and modern C#.
- Use Worker Service and dependency injection.
- Keep the architecture simple and maintainable.
- Use strongly typed configuration.
- Use async/await and `CancellationToken` correctly.
- Write unit tests for filtering and deduplication.
- Never hardcode credentials.
- Avoid unnecessary abstractions and packages.
- Do not implement all phases at once.
- Briefly explain architectural decisions.
- Build and test each phase before proceeding.
- Start with a working Telegram listener and minimal console output.
- Keep Telegram notifications disabled by default.
- Do not implement drone tracking or trajectory inference.

**First task:** Create the solution structure, implement `WTelegramClient` authentication, configure the allowlisted source list, and demonstrate receiving new messages in real time.

## 11. Suggested MVP Execution Strategy

Begin with local execution on Windows. Validate the Telegram connection and source filtering before adding persistence. Add deduplication before enabling private test notifications. Run the experiment for several days, review the metrics, and only then decide whether the system should be expanded.

Always display a disclaimer that third-party channel messages may be inaccurate or delayed and are not a substitute for official air-raid alerts.
