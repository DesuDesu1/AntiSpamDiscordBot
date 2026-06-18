# AntiSpam Discord Bot

A self-hosted Discord bot that detects and acts on spam. It watches for two common
attacks and responds automatically:

- The same (or near-identical) message posted across many channels in a short window.
- Brand-new members posting external links before they have established any history.

When it catches something it can delete the offending messages, time the user out, and
post an alert for your moderators to review.

## How it works

The bot is split into two services that talk over Kafka:

- **Gateway** holds the Discord WebSocket connection, turns gateway events into Kafka
  messages, and sends interaction responses back to Discord.
- **Bot** consumes those messages, runs spam detection, applies actions, and stores
  per-server configuration and incident history in PostgreSQL (with a Redis cache).

All settings are per server (guild) and are changed at runtime through the `/antispam`
slash command. You do not edit any files to configure a server.

## Project structure

```
src/
  AntiSpam.Contracts/   DTOs and Kafka event records shared by both services
  AntiSpam.Gateway/     Discord WebSocket plus Kafka producer
  AntiSpam.Bot/         Kafka consumers plus business logic, EF Core, Redis

deploy/
  helm/antispam/        Helm chart for Kubernetes
```

## Configuring the bot for your server

### 1. Invite the bot and grant permissions

The bot needs the following so it can act on spam and alert your team:

- **Moderate Members** to time out (mute) offenders.
- **Ban Members** to ban from the alert's Ban button.
- **Manage Messages** to delete spam.
- **View Channel**, **Send Messages**, **Embed Links**, and **Attach Files** in the
  channel you use for alerts (Attach Files is needed to show the re-hosted spam image).
- The **Message Content** gateway intent must be enabled for the bot application in the
  Discord Developer Portal, otherwise the bot cannot read message text.

Place the bot's role high in the server's role list. Discord enforces role hierarchy, so
the bot cannot mute, ban, or delete messages for the server owner or any member whose
highest role sits above the bot's. Those actions fail silently (they are logged, not
surfaced to moderators).

### 2. Pick an alert channel

Create or choose a private moderators-only channel and point the bot at it:

```
/antispam alert-channel channel:#mod-log
```

Without an alert channel the bot can still delete and mute, but no review messages are
posted.

### 3. Review the defaults and tune

Check the current settings at any time:

```
/antispam status
```

Every server starts with sensible defaults, so the bot is protective out of the box. Tune
any of the values below to fit how active and how trusted your community is.

### Command reference

All commands are subcommands of `/antispam`.

| Command | What it does | Allowed values | Default |
| --- | --- | --- | --- |
| `status` | Show all current settings | none | |
| `enable enabled:` | Turn protection on or off | true / false | true |
| `alert-channel channel:` | Where moderator alerts are posted | a text channel | none |
| `min-channels count:` | How many distinct channels the same message must hit to count as cross-channel spam | 2 to 10 | 3 |
| `similarity percent:` | How alike two messages must be to be treated as the same spam | 50 to 100 | 70 |
| `window seconds:` | Time window in which the repeated messages must appear | 30 to 600 | 120 |
| `mute enabled: duration:` | Time the offender out, and for how long (minutes) | duration 1 to 1440 | on, 60 min |
| `delete enabled:` | Delete the detected spam messages | true / false | true |
| `new-user-threshold hours:` | How long a member is treated as "new" for the link check | 1 to 168 | 24 |
| `allow-link link:` | Add a domain or URL prefix that new members may post | e.g. `youtube.com`, `twitch.tv/yourchannel` | see below |
| `remove-link link:` | Remove an entry from the allowed list | an existing entry | |
| `list-links` | Show the allowed links for this server | none | |

### Default allowed links

To avoid false positives on well-known sites, every server is seeded with a starter set of
allowed domains. New members can post links to these without being flagged:

```
youtube.com, youtu.be, twitch.tv, tenor.com, giphy.com, imgur.com,
reddit.com, twitter.com, x.com, spotify.com, soundcloud.com,
github.com, wikipedia.org
```

These are normal list entries, not a hidden setting. Run `/antispam list-links` to see
them, `/antispam remove-link x.com` to drop any you do not want, and `/antispam allow-link`
to add your own (a bare domain, or a path prefix such as `github.com/yourorg`). Removals
stick; the defaults are seeded once and are not re-added later.

### A note on tuning

- Lower `min-channels` and `similarity`, or a longer `window`, make detection more
  aggressive (and more likely to catch legitimate cross-posting).
- A higher `new-user-threshold` keeps the link check active for longer after someone joins.
- If you only want alerts and no automatic action, set `mute enabled:false` and
  `delete enabled:false` while keeping `alert-channel` configured.

## Local development

```bash
# Build everything
dotnet build

# Run the Gateway
dotnet run --project src/AntiSpam.Gateway

# Run the Bot
dotnet run --project src/AntiSpam.Bot
```

The Bot service applies EF Core migrations against PostgreSQL on startup; make sure a
database and Redis are reachable via the configured connection strings.

## Deployment

### Prerequisites on the VPS

1. K3s:
   ```bash
   curl -sfL https://get.k3s.io | sh -
   ```
2. Helm:
   ```bash
   curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
   ```
3. Clone the repository:
   ```bash
   git clone <repo> /opt/antispam
   ```

### GitHub secrets

Set these under repository Settings, Secrets:

- `VPS_HOST` server IP address
- `VPS_USER` SSH user
- `VPS_SSH_KEY` private SSH key
- `DISCORD_TOKEN` Discord bot token
- `POSTGRES_PASSWORD` PostgreSQL password

### Manual deploy

```bash
helm upgrade --install antispam ./deploy/helm/antispam \
  --set discord.token=YOUR_TOKEN \
  --set postgresql.password=YOUR_PASSWORD \
  --set gateway.image.repository=ghcr.io/YOUR_USERNAME/antispam-gateway \
  --set bot.image.repository=ghcr.io/YOUR_USERNAME/antispam-bot \
  --namespace antispam \
  --create-namespace
```

## Monitoring

```bash
# Pod status
kubectl get pods -n antispam

# Gateway logs
kubectl logs -f deployment/antispam-gateway -n antispam

# Bot logs
kubectl logs -f deployment/antispam-bot -n antispam
```
