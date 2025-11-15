# TODO Implementation Summary

## Completed TODOs

### ? 1. Default settings.json with Initial Content
**File:** `src\AspireAsbEmulatorUi.App\settings.json`

- Added comprehensive default content types: `application/json`, `text/plain`, `application/xml`, `application/octet-stream`
- Added multiple common application properties with examples
- Added sample canned message with placeholder syntax demonstration
- Fixed typo: "MT_COMNAND" ? "MT_COMMAND"

### ? 2. Monaco Editor for Message Body in Settings
**File:** `src\AspireAsbEmulatorUi.App\Components\Pages\Settings.razor`

- Replaced textarea with `StandaloneCodeEditor` component
- Added syntax highlighting for JSON, XML, and plain text
- Automatic language switching when content type changes
- Proper editor initialization and value management
- 200px height with overflow handling

### ? 3. HandyClipboardValues Component
**File:** `src\AspireAsbEmulatorUi.App\Components\HandyClipboardValues.razor`

**Features:**
- **New GUID** - Generates and copies a new GUID
- **Now** - Copies current UTC timestamp in ISO 8601 format
- **Now+1m** - Copies timestamp 1 minute in the future
- **Now+5m** - Copies timestamp 5 minutes in the future
- **Now+1h** - Copies timestamp 1 hour in the future
- **Now+1d** - Copies timestamp 1 day in the future

**UI:**
- Dark-themed buttons matching app design
- Success feedback message (auto-dismisses after 3 seconds)
- Integrated into MessageSender component
- Can be added to Settings page scenario editing

### ? 4. Save as Canned Message Feature
**File:** `src\AspireAsbEmulatorUi.App\Components\MessageSender.razor`

**Features:**
- "Save as Canned Message" button in MessageSender
- Dialog to specify entity name and scenario name
- Saves current message body, content type, application properties, and broker properties
- Pre-fills entity name with currently selected entity
- Persists to settings.json via SettingsService

### ? 5. Placeholder Syntax Processing
**File:** `src\AspireAsbEmulatorUi.App\Services\PlaceholderService.cs`

**Supported Placeholders:**
- `~newGuid~` - Generates unique GUID (each occurrence gets unique value)
- `~now~` - Current UTC timestamp in ISO 8601 format
- `~now+Xs~` - X seconds from now
- `~now+Xm~` - X minutes from now
- `~now+Xh~` - X hours from now
- `~now+Xd~` - X days from now
- `~now-Xs~` - X seconds ago (negative offsets)
- `~now-Xm~` - X minutes ago
- `~now-Xh~` - X hours ago
- `~now-Xd~` - X days ago

**Processing:**
- All placeholders are case-insensitive
- Works in message body, application properties, and broker properties
- Processed immediately before sending to ensure accurate timestamps
- Regex-based implementation for reliability

**Documentation:**
- Created `docs\PlaceholderSyntax.md` with comprehensive examples

### ? 6. Service Registration
**File:** `src\AspireAsbEmulatorUi.App\Program.cs`

- Registered `PlaceholderService` as singleton
- Available for dependency injection throughout the app

## Remaining TODOs (Not Yet Implemented)

### ? Aspire Resource and Hosting Package
**Scope:**
- Create Aspire resource definition
- Create hosting package for easy integration
- Extension methods for AppHost

### ? Extension Method to Override settings.json
**Scope:**
- Allow programmatic configuration of settings
- Useful for test scenarios

### ? API for Sending Canned Messages During Integration Tests
**Scope:**
- REST/gRPC API endpoint
- Send canned messages by entity and scenario name
- Useful for automated testing

### ? Monaco Editing Mode for settings.json
**Scope:**
- Replace textarea in Import/Export section with Monaco editor
- JSON syntax highlighting and validation
- Better editing experience for settings JSON

## Architecture Notes

### Placeholder Processing Flow
```
User Types Message ? MessageSender.Send() ? PlaceholderService.ProcessPlaceholders()
                                          ?
                                    ServiceBusService.SendMessageAsync()
                                          ?
                                    Azure Service Bus
```

### Component Relationships
```
MessageSender
  ??? HandyClipboardValues (quick value generation)
  ??? StandaloneCodeEditor (message body editing)
  ??? BrokerPropertiesEditor (broker properties)
  ??? ApplicationPropertiesEditor (app properties)
  ??? PlaceholderService (placeholder processing)

Settings
  ??? HandyClipboardValues (optional, can be added to scenarios)
  ??? StandaloneCodeEditor (body editing for canned messages)
  ??? BrokerPropertiesEditor
  ??? ApplicationPropertiesEditor
  ??? SettingsService (persistence)
```

### Key Design Decisions

1. **Placeholder Service as Singleton**: Ensures consistent behavior across the app and allows for easy testing and extension.

2. **Case-Insensitive Placeholders**: More user-friendly, works with `~newguid~`, `~NewGuid~`, etc.

3. **Unique GUIDs per Occurrence**: Each `~newGuid~` in a message gets a different GUID value, useful for complex scenarios.

4. **ISO 8601 Timestamps**: Standard format, works well with most systems and Azure Service Bus.

5. **Monaco Editor Integration**: Consistent editing experience across message body editing in both MessageSender and Settings.

## Testing Recommendations

1. **Placeholder Processing**:
   - Test with various combinations of placeholders
   - Verify unique GUID generation
   - Verify timestamp accuracy
   - Test case-insensitivity

2. **Save as Canned Message**:
   - Test with existing and new entities
   - Verify scenario name uniqueness
   - Test persistence across app restarts

3. **HandyClipboardValues**:
   - Test clipboard functionality in different browsers
   - Verify value formats
   - Test feedback messages

4. **Monaco Editor**:
   - Test syntax highlighting for JSON, XML, plain text
   - Test content type switching
   - Verify value persistence
