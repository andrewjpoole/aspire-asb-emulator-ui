# Feature Guide: New Capabilities

## 1. HandyClipboardValues Component

### What It Does
Provides quick access to commonly needed values for message testing.

### Location
- **MessageSender page**: Appears at the top of the message composition area
- **Settings page**: Can be added to canned message scenarios (optional)

### Available Values
| Button | Value | Format |
|--------|-------|--------|
| **New GUID** | Generates unique GUID | `a1b2c3d4-e5f6-7890-abcd-ef1234567890` |
| **Now** | Current UTC time | `2024-01-15T10:30:00.0000000Z` |
| **Now+1m** | 1 minute from now | `2024-01-15T10:31:00.0000000Z` |
| **Now+5m** | 5 minutes from now | `2024-01-15T10:35:00.0000000Z` |
| **Now+1h** | 1 hour from now | `2024-01-15T11:30:00.0000000Z` |
| **Now+1d** | 1 day from now | `2024-01-16T10:30:00.0000000Z` |

### Usage
1. Click any button
2. Value is copied to clipboard
3. Green success message appears
4. Paste into message body, properties, or anywhere needed

---

## 2. Save as Canned Message

### What It Does
Saves your current message composition as a reusable canned message.

### Location
**MessageSender page**: Green "Save as Canned Message" button next to "Send Message"

### Usage
1. Compose your message (body, properties, etc.)
2. Click "Save as Canned Message"
3. Enter entity name (pre-filled with current entity)
4. Enter scenario name (e.g., "happy path", "error case")
5. Click "Save"
6. Message is saved to settings.json
7. Access it later via Settings page

### Benefits
- Reuse complex message templates
- Share test scenarios with team
- Build a library of test messages
- Export/import with settings

---

## 3. Placeholder Syntax

### What It Does
Automatically replaces placeholder text with dynamic values when sending messages.

### Supported Placeholders

#### GUID Generation
```
~newGuid~
```
- Each occurrence gets a unique GUID
- Case-insensitive

#### Current Time
```
~now~
```
- UTC timestamp in ISO 8601 format
- Case-insensitive

#### Future Times
```
~now+5s~    ? 5 seconds from now
~now+5m~    ? 5 minutes from now
~now+1h~    ? 1 hour from now
~now+1d~    ? 1 day from now
```

#### Past Times
```
~now-5s~    ? 5 seconds ago
~now-5m~    ? 5 minutes ago
~now-1h~    ? 1 hour ago
~now-1d~    ? 1 day ago
```

### Example Usage

#### Before Sending:
```json
{
  "orderId": "~newGuid~",
  "customerId": "~newGuid~",
  "timestamp": "~now~",
  "scheduledDelivery": "~now+5m~",
  "expiresAt": "~now+1d~"
}
```

#### After Sending:
```json
{
  "orderId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "customerId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "timestamp": "2024-01-15T10:30:00.0000000Z",
  "scheduledDelivery": "2024-01-15T10:35:00.0000000Z",
  "expiresAt": "2024-01-16T10:30:00.0000000Z"
}
```

### Where Placeholders Work
- ? Message body
- ? Application properties
- ? Broker properties
- ? Canned messages

### Tips
- Use placeholders in canned messages for dynamic test data
- Combine HandyClipboardValues for manual entry and placeholders for automated sending
- Timestamps are always in UTC
- Each `~newGuid~` generates a unique value

---

## 4. Monaco Editor for Message Bodies

### What It Does
Provides syntax-highlighted code editing for message bodies.

### Location
- **MessageSender page**: Message body editing area
- **Settings page**: Canned message body editing (when scenario is expanded)

### Features
- ? **Syntax Highlighting**: JSON, XML, and plain text
- ?? **Dark Theme**: Matches app design
- ?? **Auto Language Switching**: Changes based on content type
- ?? **Auto Indentation**: Proper JSON/XML formatting
- ?? **Bracket Matching**: Highlights matching brackets
- ?? **Code Folding**: Collapse sections of code

### Keyboard Shortcuts
| Action | Windows/Linux | Mac |
|--------|---------------|-----|
| Format Document | `Shift+Alt+F` | `Shift+Option+F` |
| Find | `Ctrl+F` | `Cmd+F` |
| Replace | `Ctrl+H` | `Cmd+Option+F` |
| Comment Line | `Ctrl+/` | `Cmd+/` |

---

## 5. Enhanced Settings Page

### New Capabilities

#### Content Type Selection
- Dropdown with pre-configured content types
- Add custom content types
- Remove unused content types
- Set default content type for new messages

#### Canned Messages
- Expand/collapse scenario details
- Monaco editor for body editing
- Application and broker properties editors
- Initialize corrupted messages
- Remove entities or scenarios

#### Import/Export
- Export all settings as JSON
- Import settings from JSON
- Backup and share configurations
- Version control friendly

---

## Workflow Examples

### Example 1: Creating a Test Message

1. Go to MessageSender
2. Select target entity
3. Use **HandyClipboardValues** to copy a GUID
4. Paste into message body or properties
5. Use placeholders for dynamic values:
   ```json
   {
     "id": "~newGuid~",
     "timestamp": "~now~",
     "scheduledFor": "~now+5m~"
   }
   ```
6. Add application properties
7. Click **Send Message**
8. Placeholders are replaced automatically
9. Click **Save as Canned Message** to reuse later

### Example 2: Managing Canned Messages

1. Go to Settings page
2. Navigate to Canned Messages section
3. Click "Add Entity" for a new queue/topic
4. Click "Add Scenario" for a test case
5. Expand the scenario
6. Use **Monaco editor** to edit message body
7. Use placeholders for dynamic data
8. Add application and broker properties
9. Click "Save Settings"
10. Return to MessageSender to use the canned message

### Example 3: Sharing Test Scenarios

1. Go to Settings page
2. Configure canned messages and content types
3. Click "Export JSON"
4. Share the JSON with your team
5. Team members click "Import JSON"
6. Paste and import
7. Everyone has the same test scenarios

---

## Tips & Best Practices

### Placeholder Usage
- ? Use `~newGuid~` for unique identifiers
- ? Use `~now~` for timestamps
- ? Use `~now+5m~` for scheduled/future events
- ? Use `~now-1h~` for historical data
- ? Combine placeholders in complex scenarios

### Canned Messages
- ?? Use descriptive scenario names ("happy-path", "error-invalid-id")
- ??? Organize by entity (queue/topic)
- ?? Export settings regularly as backup
- ?? Version control settings.json
- ?? Document special scenarios in scenario names

### Message Composition
- ?? Use Monaco editor for complex JSON/XML
- ?? Use HandyClipboardValues for quick values
- ?? Use placeholders for repeatable tests
- ?? Save complex messages as canned messages
- ?? Test with different content types

### Settings Management
- ?? Add all content types you'll use
- ??? Add common application properties
- ?? Export before major changes
- ?? Import to restore or share
- ?? Keep settings.json in source control
