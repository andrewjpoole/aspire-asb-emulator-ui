using System.Data.Common;
using AspireAsbEmulatorUi.App.Services;
using Microsoft.Extensions.Configuration;

namespace AspireAsbEmulatorUi.App.Tests;

public class ServiceBusAdministrationOptionsTests
{
    [Test]
    [Arguments("http://broker:5672", true)]
    [Arguments("https://broker:5301", false)]
    public async Task Endpoint_PreservesNamespaceCredentialsAndHonorsTransport(string endpoint, bool plaintext)
    {
        var options = Create(new()
        {
            ["ASB_RESOURCE_NAME"] = "custom",
            ["ConnectionStrings:custom"] = "Endpoint=sb://data:5672;SharedAccessKeyName=my-namespace;SharedAccessKey=emulator;UseDevelopmentEmulator=true;EntityPath=queue;",
            ["ASB_MANAGEMENT_ENDPOINT"] = endpoint
        });
        var connection = new DbConnectionStringBuilder { ConnectionString = options.ConnectionString! };

        await Assert.That((string)connection["SharedAccessKeyName"]).IsEqualTo("my-namespace");
        await Assert.That((string)connection["SharedAccessKey"]).IsEqualTo("emulator");
        await Assert.That((string)connection["Endpoint"]).IsEqualTo(endpoint.Replace("http://", "sb://").Replace("https://", "sb://"));
        await Assert.That(bool.Parse(connection["UseDevelopmentEmulator"].ToString()!)).IsEqualTo(plaintext);
        await Assert.That(connection.ContainsKey("EntityPath")).IsFalse();
    }

    [Test]
    public async Task ExplicitConnectionString_TakesPrecedenceOverEndpoint()
    {
        const string connection = "Endpoint=sb://broker:5672;SharedAccessKeyName=namespace;SharedAccessKey=emulator;UseDevelopmentEmulator=true;";
        var options = Create(new() { ["ASB_MANAGEMENT_CONNECTIONSTRING"] = connection, ["ASB_MANAGEMENT_ENDPOINT"] = "invalid" });
        await Assert.That(options.ConnectionString).IsEqualTo(connection);
    }

    [Test]
    [Arguments("not-a-url")]
    [Arguments("http://user:secret@broker:5300")]
    [Arguments("http://broker:5300/path")]
    [Arguments("http://broker:5300/?secret=value")]
    public async Task InvalidEndpoint_IsActionableWithoutLeakingConfiguration(string endpoint)
    {
        try
        {
            Create(new() { ["ASB_MANAGEMENT_ENDPOINT"] = endpoint });
            throw new Exception("Expected configuration failure");
        }
        catch (InvalidOperationException ex)
        {
            await Assert.That(ex.Message.Contains("ASB_MANAGEMENT_ENDPOINT")).IsTrue();
            await Assert.That(ex.ToString().Contains(endpoint)).IsFalse();
            await Assert.That(ex.ToString().Contains("secret")).IsFalse();
        }
    }

    [Test]
    [Arguments("invalid")]
    [Arguments("Administration")]
    public async Task InvalidModeOrMissingAdministrationConfiguration_FailsClearly(string mode)
    {
        await Assert.That(() => Create(new() { ["ASB_ENTITY_DISCOVERY"] = mode })).Throws<InvalidOperationException>();
    }

    private static ServiceBusAdministrationOptions Create(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
