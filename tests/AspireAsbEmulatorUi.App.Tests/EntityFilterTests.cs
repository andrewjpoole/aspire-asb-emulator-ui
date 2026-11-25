using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;

namespace AspireAsbEmulatorUi.App.Tests;

public class EntityFilterTests
{
    [Test]
    [Arguments("orders*", "OrdersInboundNew", true)]
    [Arguments("orders*", "old-orders", false)]
    [Arguments("orders*", "orders", true)]
    [Arguments("*NEW", "orders-inbound-new", true)]
    [Arguments("*new", "orders-new-archive", false)]
    [Arguments("*orders*", "old-orders-archive", true)]
    [Arguments("orders*new", "OrdersInboundNew", true)]
    [Arguments("orders*new", "ordersnew", true)]
    [Arguments("orders*new", "orders-new-archive", false)]
    [Arguments("orders**new", "orders-inbound-new", true)]
    [Arguments("*orders*new", "old-orders-inbound-new", true)]
    [Arguments("*", "", true)]
    [Arguments("**", "OrdersInboundNew", true)]
    [Arguments("*new", "", false)]
    [Arguments("orders*", "OtherInboundNames", false)]
    [Arguments("orders[1].?+($)*", "orders[1].?+($)-new", true)]
    [Arguments("orders[1].?+($)*", "orders1Xother-new", false)]
    [Arguments("orders?*", "orders-new", false)]
    public async Task Wildcards_MatchWholeNamesWithOnlyStarSpecial(string filter, string name, bool expected)
    {
        var entity = new ServiceBusEntityInfo { Name = name, EntityType = "Queue", Id = 42 };
        await Assert.That(EntityFilter.Matches(entity, filter)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("OrdersInboundNew", "oin", true)]
    [Arguments("ordersInboundNew", "OIN", true)]
    [Arguments("orders-inbound-new", "oin", true)]
    [Arguments("orders_inbound_new", "oi", true)]
    [Arguments("orders.inbound.new", "oin", true)]
    [Arguments("orders inbound new", "oin", true)]
    [Arguments("orders/inbound:new", "oin", true)]
    [Arguments(".orders--inbound__new", "oin", true)]
    [Arguments("HTTPOrderInbound", "hoi", true)]
    [Arguments("HTTPOrderInbound", "ho", true)]
    [Arguments("HTTPOrderInbound", "htoi", false)]
    [Arguments("OrdersOtherInboundNew", "oin", false)]
    [Arguments("OrdersInboundNew", "oinx", false)]
    [Arguments("OrdersInboundNew", "oni", false)]
    public async Task Initials_MatchWordPrefixesNotArbitrarySubsequences(string name, string filter, bool expected)
    {
        var entity = new ServiceBusEntityInfo { Name = name, EntityType = "Queue" };
        await Assert.That(EntityFilter.Matches(entity, filter)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("", true)]
    [Arguments(" \t ", true)]
    [Arguments("BOuND", true)]
    [Arguments("42", true)]
    [Arguments("4", false)]
    [Arguments("42*", false)]
    [Arguments("Orders?", false)]
    public async Task LiteralNamesAndExactIds_KeepExistingBehavior(string? filter, bool expected)
    {
        var entity = new ServiceBusEntityInfo { Name = "OrdersInboundNew", EntityType = "Queue", Id = 42 };
        await Assert.That(EntityFilter.Matches(entity, filter)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("oin")]
    [Arguments("orders*")]
    [Arguments("events|orders*")]
    [Arguments("42")]
    public async Task Apply_RetainsMatchingSubscriptionsParentWithoutUnrelatedChildren(string filter)
    {
        var parent = new ServiceBusEntityInfo { Name = "Events", EntityType = "Topic", Id = 1 };
        var match = new ServiceBusEntityInfo
        {
            Name = "Events|OrdersInboundNew", ParentTopic = "Events", EntityType = "Subscription", Id = 42
        };
        var sibling = new ServiceBusEntityInfo
        {
            Name = "Events|Billing", ParentTopic = "Events", EntityType = "Subscription", Id = 43
        };
        var unrelated = new ServiceBusEntityInfo { Name = "Other", EntityType = "Topic", Id = 2 };
        var queue = new ServiceBusEntityInfo { Name = "OtherQueue", EntityType = "Queue", Id = 3 };
        List<ServiceBusEntityInfo> entities = [queue, match, unrelated, parent, sibling];

        var filtered = EntityFilter.Apply(entities, filter);

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(filtered[0], match)).IsTrue();
        await Assert.That(ReferenceEquals(filtered[1], parent)).IsTrue();
        await Assert.That(entities.Count).IsEqualTo(5);
        await Assert.That(match.Id).IsEqualTo(42);
        await Assert.That(match.Name).IsEqualTo("Events|OrdersInboundNew");
    }

    [Test]
    public async Task Apply_MatchingTopicDoesNotExposeNonmatchingChildren()
    {
        var parent = new ServiceBusEntityInfo { Name = "Events", EntityType = "Topic" };
        var child = new ServiceBusEntityInfo
        {
            Name = "Events|OrdersInboundNew", ParentTopic = "Events", EntityType = "Subscription"
        };

        var filtered = EntityFilter.Apply([parent, child], "*events");

        await Assert.That(filtered.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(filtered[0], parent)).IsTrue();
    }
}
