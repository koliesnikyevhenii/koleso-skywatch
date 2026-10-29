# Skywatch runbook

How to run Skywatch for the experiment on a Linux VPS with Docker, and as a Windows Service. Spec: `docs/spec.md`;
plan phase P7.

All commands below run in this `deploy/` folder unless stated otherwise. The Compose project is named `skywatch`,
so the data volume is `skywatch_skywatch-data`, mounted at `/app/data` in the container. It holds:

| File | What | Never delete it for a DB reset |
|---|---|---|
| `session.dat` | Encrypted Telegram session of the reader account (`Telegram:SessionKey`) | yes |
| `updates.state` | WTelegramClient update state (catch-up after a restart) | yes |
| `skywatch.db`, `skywatch.db-wal`, `skywatch.db-shm` | SQLite database | — |

The session file is a full login to the reader account (risk R2): never copy it anywhere else, never commit it.

## 1. Provision the VPS (minimal)

1. A small Linux VPS (1 vCPU, 1 GB RAM is plenty; N2).
2. Install Docker Engine with the Compose plugin (docs.docker.com/engine/install).
3. Make sure the clock is synchronized: `timedatectl` shows `System clock synchronized: yes` and
   `NTP service: active`. Otherwise `sudo timedatectl set-ntp true`. Staleness depends on it (risk R4).
4. Clone the repository: `git clone <repo url> && cd koleso-skywatch/deploy`.

## 2. Configure

1. `cp .env.example .env && chmod 600 .env`.
2. Fill in every empty value in `.env`:
   - `Telegram__ApiId`, `Telegram__ApiHash`: from my.telegram.org for the **dedicated** reader account.
   - `Telegram__PhoneNumber`: the dedicated account's number.
   - `Telegram__SessionKey`: a long random string, e.g. `openssl rand -base64 32`. Keep it: a session file only
     opens with the key it was created with.
   - `Notifications__BotToken`, `Notifications__TargetChatId` (the private test group, negative id),
     `Notifications__LabelerUserIds__0` (and `__1`, ...).
   - The sources come later (step 4).
3. A line with an empty value fails at startup (an empty number or flag cannot be parsed): fill it or delete it.

## 3. Get a session

Either log in inside the container (needs the code from Telegram, and the 2FA password if set):

```sh
docker compose build
docker compose run --rm skywatch login
```

Or copy a session created elsewhere with `dotnet run --project src/Skywatch.Worker -- login`. Copy the file **and**
use the same `Telegram__SessionKey` in `.env`:

```sh
docker compose run --rm -v "$PWD/session.dat:/tmp/session.dat:ro" --entrypoint cp skywatch /tmp/session.dat /app/data/session.dat
rm session.dat
```

## 4. Choose the sources

```sh
docker compose run --rm skywatch list-chats
```

Put the chosen marked ids (`-100…`) and names into `.env` as `Monitoring__Sources__0__ChatId`,
`Monitoring__Sources__0__Name`, `Monitoring__Sources__1__…` and so on.

## 5. Pre-run checklist

- [ ] The dedicated reader account is warmed up (used manually for some days; risk R1) and is a member of every
      source.
- [ ] The bot is in the private test group, and `Notifications__TargetChatId` is that group.
- [ ] `.env` has `Notifications__Enabled=true`, `Storage__StoreUnmatchedText=true` and
      `Storage__UnmatchedRetentionDays=8` (experiment length + 1 day, so `rescan` still sees every unmatched text;
      the spec default stays 3).
- [ ] NTP is active on the VPS (step 1.3).
- [ ] After the start, the log shows the display time zone line with the correct Kyiv time (see 6).

## 6. Start, stop, logs

```sh
docker compose up -d --build     # start (or update after git pull)
docker compose logs -f           # JSON logs; rotated by Docker (5 × 10 MB)
docker compose restart           # session, update state, DB and Pending notifications are kept
docker compose stop              # graceful: drains queued messages, writes the Stopped event (≤ 30 s)
docker compose down              # stop and remove the container; the volume stays
```

The start log contains `Display time zone Europe/Kyiv; local time now …`: check that it is the current Kyiv time.
Log timestamps themselves are UTC (`…Z`).

The container restarts by itself after a crash (`restart: unless-stopped`, Docker backs off up to 1 minute).

### When the log says `Critical … run login`

The session is missing or was rejected (for example, terminated from another device). Then:

```sh
docker compose stop
docker compose run --rm skywatch login
docker compose up -d
```

## 7. Data for the analysis

### Missed mentions (success metric 6)

```sh
docker compose run --rm skywatch rescan
docker compose run --rm skywatch rescan --pattern "красноп"
```

`rescan` reads the stored unmatched texts and prints the ones the patterns (or the one regex) match now. It is
read-only and can run while the service runs.

### Copy the database

The database contains message texts from the sources (personal data, risk R9): keep the copy private and delete it
after the analysis.

```sh
docker compose stop
docker run --rm -v skywatch_skywatch-data:/data -v "$PWD":/backup alpine \
  sh -c 'cd /data && tar czf /backup/skywatch-db-$(date +%F).tgz skywatch.db*'
docker compose start
```

### Reset the database

Starts the experiment from an empty database. The session and the update state are kept.

```sh
docker compose stop
docker run --rm -v skywatch_skywatch-data:/data alpine rm -f /data/skywatch.db /data/skywatch.db-wal /data/skywatch.db-shm
docker compose start             # migrations create a new database
```

Do **not** delete `session.dat` (a new login would be needed) or `updates.state`.

## 8. Windows Service (alternative)

For running on a Windows machine instead of the VPS. In PowerShell as administrator, from the repository root:

```powershell
dotnet publish src/Skywatch.Worker -c Release -o C:\Skywatch
cd C:\Skywatch
.\Skywatch.Worker.exe login        # creates C:\Skywatch\data\session.dat
.\Skywatch.Worker.exe list-chats
```

Configuration: create `C:\Skywatch\appsettings.Production.json` with the same keys as `.env`, in JSON form
(`"Telegram": { "ApiId": … }`). The file is outside the repository; restrict it to administrators and the service
account. User Secrets do not apply to a service account.

```powershell
sc.exe create Skywatch binPath= "C:\Skywatch\Skywatch.Worker.exe" start= delayed-auto
sc.exe failure Skywatch reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start Skywatch
```

The service runs from `C:\Skywatch`, so `data\` is `C:\Skywatch\data`. Warnings and errors go to the Windows Event
Log (Application, source `Skywatch.Worker`). `sc.exe stop Skywatch` stops it gracefully; `sc.exe delete Skywatch` removes it.
By default it runs as LocalSystem; a dedicated local account (`obj= .\skywatch password= …`) with access to
`C:\Skywatch` only is better.
