// Minimal surface of vPilot's plugin API used by UnsubAutoATC. See PluginsStub.csproj.
// If the plugin starts using more of IBroker, add the matching members here, copying the
// signatures from the real RossCarlson.Vatsim.Vpilot.Plugins.dll.
using System;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

namespace RossCarlson.Vatsim.Vpilot.Plugins
{
    public interface IPlugin
    {
        string Name { get; }

        void Initialize(IBroker broker);
    }

    public interface IBroker
    {
        event EventHandler<NetworkConnectedEventArgs> NetworkConnected;

        void PostDebugMessage(string message);
    }
}

namespace RossCarlson.Vatsim.Vpilot.Plugins.Events
{
    public class NetworkConnectedEventArgs : EventArgs
    {
    }
}
