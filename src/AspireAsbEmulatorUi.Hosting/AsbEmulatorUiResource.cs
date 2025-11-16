using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.AsbEmulatorUi.Integration;

// ProjectResource, ParameterResource, ContainerResource or ExecutableResource
public sealed class AsbEmulatorUiResource(string name) : ContainerResource(name), IResourceWithServiceDiscovery
{
}