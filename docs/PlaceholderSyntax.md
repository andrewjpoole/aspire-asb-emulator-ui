# Placeholder Syntax for Canned Messages

When creating canned messages or sending messages through the UI, you can use placeholder syntax that will be automatically replaced with dynamic values when the message is sent.

## Supported Placeholders

### GUID Generation
- `~newGuid~` - Generates a new unique GUID
- Case-insensitive (e.g., `~newguid~`, `~NEWGUID~` also work)
- Each occurrence generates a unique GUID

**Example:**
```json
{
  "orderId": "~newGuid~",
  "customerId": "~newGuid~"
}
```

### Current Timestamp
- `~now~` - Current UTC time in ISO 8601 format
- Case-insensitive

**Example:**
```json
{
  "timestamp": "~now~",
  "createdAt": "~now~"
}
```

### Time Offsets (Future)
Add time to the current UTC timestamp:

- `~now+5s~` - 5 seconds from now
- `~now+5m~` - 5 minutes from now
- `~now+1h~` - 1 hour from now
- `~now+1d~` - 1 day from now

**Supported units:**
- `s` - seconds
- `m` - minutes
- `h` - hours
- `d` - days

**Example:**
```json
{
  "scheduledTime": "~now+5m~",
  "expiresAt": "~now+1d~"
}
```

### Time Offsets (Past)
Subtract time from the current UTC timestamp:

- `~now-5s~` - 5 seconds ago
- `~now-5m~` - 5 minutes ago
- `~now-1h~` - 1 hour ago
- `~now-1d~` - 1 day ago

**Example:**
```json
{
  "startedAt": "~now-1h~",
  "lastSeen": "~now-5m~"
}
```

## Complete Example

### Canned Message Definition
```json
{
  "eventId": "~newGuid~",
  "eventType": "UserCreated",
  "timestamp": "~now~",
  "data": {
    "userId": "~newGuid~",
    "username": "john.doe",
    "email": "john.doe@example.com",
    "registeredAt": "~now~",
    "emailVerificationDeadline": "~now+1d~"
  },
  "metadata": {
    "correlationId": "~newGuid~",
    "processedAfter": "~now+5m~"
  }
}
```

### When Sent, Placeholders Are Replaced
```json
{
  "eventId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "eventType": "UserCreated",
  "timestamp": "2024-01-15T10:30:00.0000000Z",
  "data": {
    "userId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
    "username": "john.doe",
    "email": "john.doe@example.com",
    "registeredAt": "2024-01-15T10:30:00.0000000Z",
    "emailVerificationDeadline": "2024-01-16T10:30:00.0000000Z"
  },
  "metadata": {
    "correlationId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
    "processedAfter": "2024-01-15T10:35:00.0000000Z"
  }
}
```

## Using Placeholders in Application Properties and Broker Properties

Placeholders work in all fields:
- Message body
- Application properties
- Broker properties

**Example Application Properties:**
```
MessageType: MT_EVENT
CorrelationId: ~newGuid~
Timestamp: ~now~
```

**Example Broker Properties:**
```
MessageId: ~newGuid~
ScheduledEnqueueTime: ~now+5m~
CorrelationId: ~newGuid~
```

## Notes

- All timestamps are in UTC
- ISO 8601 format is used for all datetime values
- Placeholders are case-insensitive
- Placeholders are processed just before sending, ensuring accurate timestamps
- Each `~newGuid~` occurrence generates a unique GUID
