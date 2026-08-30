# XtremeIdiots.Portal.Server.Events.Abstractions.V1

Service Bus event contracts for the XtremeIdiots Portal server event pipeline.

This package contains the shared Data Transfer Objects (DTOs) and Service Bus
queue-name constants used by both sides of the pipeline:

- **Publisher** — `portal-server-agent` publishes server events onto Service Bus queues.
- **Consumer** — the `portal-server-events` Processor Function App subscribes to those queues.

Keeping these contracts in a single versioned package ensures the publisher and
consumer agree on message shapes and queue names.

## Contents

- `Queues` — string constants for every Service Bus queue name (e.g. `player-connected`, `chat-message`, `ban-applied`).
- `Events` — the event DTOs carried on each queue, all deriving from `ServerEventBase`.

## Usage

```csharp
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

// Reference a queue name constant
var queueName = Queues.PlayerConnected;

// Construct an event DTO
var evt = new PlayerConnectedEvent
{
    // ... populate properties
};
```

## Versioning

This package is versioned with [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning).
Breaking changes to DTOs or queue-name constants require a major version bump and a
coordinated update in the publishing repository (`portal-server-agent`).
