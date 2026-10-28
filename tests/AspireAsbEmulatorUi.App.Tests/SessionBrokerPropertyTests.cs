using AspireAsbEmulatorUi.App.Components;
using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Tests;

public class SessionBrokerPropertyTests
{
    [Test]
    public async Task FindOrAddRow_CreatesOnePreselectedDraft()
    {
        List<BrokerPropertiesEditor.PropertyItem> properties = [];
        var first = SessionBrokerProperty.FindOrAddRow(properties);
        var second = SessionBrokerProperty.FindOrAddRow(properties);
        await Assert.That(first).IsEqualTo(second);
        await Assert.That(properties.Count).IsEqualTo(1);
        await Assert.That(properties[first].Key).IsEqualTo("SessionId");
        await Assert.That(properties[first].Value).IsEqualTo("");
        await Assert.That(properties[first].IsEditing).IsTrue();
        await Assert.That(properties[first].OriginalKey).IsNull();
    }

    [Test]
    public async Task FindOrAddRow_ReusesExistingCaseInsensitiveValue()
    {
        List<BrokerPropertiesEditor.PropertyItem> properties = [new() { Key = "sessionid", Value = "Order-A" }];
        var index = SessionBrokerProperty.FindOrAddRow(properties);
        await Assert.That(index).IsEqualTo(0);
        await Assert.That(properties.Count).IsEqualTo(1);
        await Assert.That(properties[index].Value).IsEqualTo("Order-A");
        await Assert.That(properties[index].IsEditing).IsFalse();
    }

    [Test]
    public async Task FindOrAddRow_PreservesRenamedUnsavedDraftAndOriginals()
    {
        List<BrokerPropertiesEditor.PropertyItem> properties = [
            new() { Key = "MessageId", OriginalKey = "SessionId", OriginalValue = "original", Value = "draft", IsEditing = true }];
        var index = SessionBrokerProperty.FindOrAddRow(properties);
        await Assert.That(index).IsEqualTo(0);
        await Assert.That(properties.Count).IsEqualTo(1);
        await Assert.That(properties[index].Value).IsEqualTo("draft");
        await Assert.That(properties[index].OriginalValue).IsEqualTo("original");
        await Assert.That(SessionBrokerProperty.GetValidationError(properties, true)).IsNotNull();
    }

    [Test]
    public async Task Validation_RequiresIdOnlyForKnownSessionEntities()
    {
        List<BrokerPropertiesEditor.PropertyItem> properties = [];
        await Assert.That(SessionBrokerProperty.GetValidationError(properties, true)).IsNotNull();
        await Assert.That(SessionBrokerProperty.GetValidationError(properties, false)).IsNull();
        await Assert.That(SessionBrokerProperty.GetValidationError(properties, null)).IsNull();
        properties.Add(new() { Key = "SessionId", Value = "Order-A" });
        await Assert.That(SessionBrokerProperty.GetValidationError(properties, true)).IsNull();
        properties.Add(new() { Key = "sessionid", Value = "duplicate" });
        await Assert.That(SessionBrokerProperty.GetValidationError(properties, false)).IsNotNull();
    }

    [Test]
    public async Task LiteralSessionId_IsSharedByBatchButExplicitPlaceholderIsEvaluatedPerMessage()
    {
        var id = Guid.NewGuid().ToString();
        List<BrokerPropertiesEditor.PropertyItem> properties = [new() { Key = "SessionId", Value = id }];
        var placeholders = new Services.PlaceholderService();
        var sentIds = Enumerable.Range(0, 3)
            .Select(_ => placeholders.ProcessPlaceholders(SessionBrokerProperty.GetValue(properties))).ToList();
        await Assert.That(sentIds.All(value => value == id)).IsTrue();
        properties[0].Value = "~newGuid~";
        await Assert.That(placeholders.ProcessPlaceholders(SessionBrokerProperty.GetValue(properties)))
            .IsNotEqualTo(placeholders.ProcessPlaceholders(SessionBrokerProperty.GetValue(properties)));
        properties.Clear();
        properties.Add(new() { Key = "SessionId", Value = "canned-session" });
        await Assert.That(SessionBrokerProperty.GetValue(properties)).IsEqualTo("canned-session");
        properties.Clear();
        await Assert.That(SessionBrokerProperty.GetValue(properties)).IsEqualTo("");
    }
}
