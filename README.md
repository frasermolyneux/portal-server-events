# Portal Server Events

Service Bus event contracts and processor functions for the XtremeIdiots portal server event pipeline.

## Architecture

This repository contains:

1. **Abstractions NuGet package** — Event DTOs and queue name constants shared between the agent (publisher) and processor (consumer)
2. **Processor Function App** — Azure Functions that subscribe to Service Bus queues and process events (persistence, moderation, GeoIP enrichment, live stats)

## Project Structure

```
src/
├── XtremeIdiots.Portal.Server.Events.Abstractions.V1/    # NuGet package
│   ├── Events/           # Event DTOs (ServerEventBase, PlayerConnectedEvent, etc.)
│   └── Queues.cs         # Queue name constants
├── XtremeIdiots.Portal.Server.Events.Processor.App/       # Azure Functions
│   └── Functions/        # Queue-triggered processors
└── XtremeIdiots.Portal.Server.Events.Processor.App.Tests/
```

## Event Types

| Event | Queue | Published When |
|-------|-------|----------------|
| `PlayerConnectedEvent` | `player-connected` | Player joins server |
| `PlayerDisconnectedEvent` | `player-disconnected` | Player leaves server |
| `ChatMessageEvent` | `chat-message` | Player sends chat |
| `MapVoteEvent` | `map-vote` | Player types !like/!dislike |
| `ServerConnectedEvent` | `server-connected` | Agent starts monitoring |
| `MapChangeEvent` | `map-change` | Server changes map |
| `ServerStatusEvent` | `server-status` | Periodic snapshot (60s) |
| `BanFileChangedEvent` | `ban-file-changed` | Ban file modified on server |

## Platform Settings Contracts

Chat command and welcome message settings consumed by the processor use typed contracts from `XtremeIdiots.Portal.Settings.Contracts.V1`.

See `docs/platform-settings-contracts.md` for architecture details, migration context, and troubleshooting steps.
