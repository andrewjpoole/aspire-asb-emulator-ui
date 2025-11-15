# Aspire ASB Emulator UI

A Blazor-based web UI for exploring and testing Azure Service Bus entities in the [Azure Service Bus Emulator](https://learn.microsoft.com/azure/service-bus-messaging/test-locally-with-service-bus-emulator) in Aspire.

![.NET 10](https://img.shields.io/badge/.NET-10-blue) ![Blazor](https://img.shields.io/badge/Blazor-Interactive%20Server-purple) ![Tailwind CSS](https://img.shields.io/badge/Tailwind-CSS-38B2AC)

## Features

### 🔍 Entity Explorer
- View all **queues** and **topics** from your ASB emulator
- Real-time message counts (Active & Dead-Letter Queue)
- Auto-refresh mode with manual override
- Filter entities by name or ID

### 📨 Message Sender
- Send messages to queues or topics
- **Monaco Editor** with syntax highlighting for JSON, XML, plain text
- Configure broker properties (MessageId, CorrelationId, SessionId, TTL, ScheduledEnqueueTime, etc.)
- Configure application properties
- **Placeholder syntax** for dynamic test data
- **Save as Canned Message** for reuse
- **Quick Values** clipboard helper (new GUIDs, timestamps)

### 👀 Message Viewer
- Peek or Receive messages from queues
- Monaco Editor for message body viewing
- Display broker and application properties

### ⚙️ Settings Management
- Manage content types
- Configure common application properties
- **Canned Messages** library with Monaco editor
- Import/Export settings as JSON

## Quick Start

```bash
git clone https://github.com/andrewjpoole/aspire-asb-emulator-ui.git
cd aspire-asb-emulator-ui/aspire/AppHost
dotnet run
```

Access the UI via the Aspire dashboard.

## Placeholder Syntax

Use placeholders in message bodies for dynamic test data:

| Placeholder | Example Result |
|-------------|----------------|
| `~newGuid~` | `a1b2c3d4-e5f6-7890-abcd-ef1234567890` |
| `~now~` | `2024-01-15T10:30:00.0000000Z` |
| `~now+5m~` | 5 minutes from now |
| `~now+1h~` | 1 hour from now |
| `~now+1d~` | 1 day from now |
| `~now-5m~` | 5 minutes ago |

**Example:**
```json
{
  "orderId": "~newGuid~",
  "customerId": "~newGuid~",
  "timestamp": "~now~",
  "scheduledDelivery": "~now+5m~",
  "expiresAt": "~now+1d~"
}
```

## Important Notes

**Azure Service Bus Message Flow:**
- **Queues**: Send → Queue → Receive ✅
- **Topics**: Send → Topic → Subscriptions → Receive ✅
  - Send messages **TO topics** (not subscriptions)
  - Topics automatically distribute to subscriptions
  - Subscriptions are hidden in the UI (by design)
  - Topic active count may show 0 (messages are in subscriptions)

## Tech Stack
- **Blazor Interactive Server** - Real-time UI
- **Tailwind CSS** - Modern styling
- **Monaco Editor** - VS Code-powered editing
- **Azure Service Bus Client SDK** - Native ASB operations
- **.NET 10** - Latest .NET

## Documentation
- [PlaceholderSyntax.md](docs/PlaceholderSyntax.md) - Complete placeholder reference
- [FeatureGuide.md](docs/FeatureGuide.md) - Detailed feature documentation

## License
MIT License
