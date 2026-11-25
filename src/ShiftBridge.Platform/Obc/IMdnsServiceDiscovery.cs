using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Platform.Obc;

public interface IMdnsAdvertiser : IDisposable
{
    void Advertise(string instanceName, string serviceType, ushort port, IDictionary<string, string> txt,
        Action<string>? log = null);
    void Stop();
}

public interface IMdnsServiceDiscovery
{
    IMdnsAdvertiser CreateAdvertiser();
    Task<List<DiscoveredTrainer>> BrowseTrainersAsync(TimeSpan duration, CancellationToken ct = default);
}
