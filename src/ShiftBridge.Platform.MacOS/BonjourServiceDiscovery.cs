using System.Net;
using System.Text;
using CoreFoundation;
using Foundation;
using ShiftBridge.Platform.Obc;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Platform.MacOS;

// NSNetService can publish a port that ShiftBridge's existing TCP listener already owns.
#pragma warning disable CA1422
public sealed class BonjourServiceDiscovery : IMdnsServiceDiscovery
{
    private const string Domain = "local.";
    private const string TrainerService = "_wahoo-fitness-tnp._tcp.";

    public IMdnsAdvertiser CreateAdvertiser() => new Advertiser();

    public async Task<List<DiscoveredTrainer>> BrowseTrainersAsync(TimeSpan duration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var search = await OnMainAsync(() => new TrainerSearch(duration));
        try
        {
            await Task.WhenAny(Task.Delay(duration, ct), search.Failure.Task);
            if (search.Failure.Task.IsCompleted) throw new IOException(await search.Failure.Task);
            return await OnMainAsync(() => search.Results.Values.ToList());
        }
        finally { await OnMainAsync(() => { search.Dispose(); return true; }); }
    }

    private static Task<T> OnMainAsync<T>(Func<T> action)
    {
        if (NSThread.IsMain) return Task.FromResult(action());
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try { result.SetResult(action()); }
            catch (Exception ex) { result.SetException(ex); }
        });
        return result.Task;
    }

    private sealed class Advertiser : IMdnsAdvertiser
    {
        private NSNetService? _service;

        public void Advertise(string instanceName, string serviceType, ushort port, IDictionary<string, string> txt,
            Action<string>? log = null) => OnMainAsync(() =>
        {
            StopService();
            var service = new NSNetService(Domain, serviceType.TrimEnd('.') + ".", instanceName, port);
            try
            {
                using var entries = new NSMutableDictionary();
                foreach (var (key, value) in txt)
                {
                    using var k = new NSString(key);
                    using var v = NSData.FromArray(Encoding.UTF8.GetBytes(value));
                    entries[k] = v;
                }
                using var record = NSNetService.DataFromTxtRecord(entries);
                if (!service.SetTxtRecordData(record)) throw new IOException("Bonjour rejected the TXT record.");

                service.Published += (_, _) => log?.Invoke($"Bonjour: {service.Name} ({service.Type}, port {port})");
                service.PublishFailure += (_, e) => log?.Invoke($"Bonjour: {e.Errors}");
                service.Schedule(NSRunLoop.Main, NSRunLoopMode.Common);
                service.Publish();
                _service = service;
                return true;
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }).GetAwaiter().GetResult();

        public void Stop() => OnMainAsync(() => { StopService(); return true; }).GetAwaiter().GetResult();

        private void StopService()
        {
            _service?.Stop();
            _service?.Unschedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _service?.Dispose();
            _service = null;
        }

        public void Dispose() => Stop();
    }

    private sealed class TrainerSearch : IDisposable
    {
        private readonly NSNetServiceBrowser _browser = new();
        private readonly HashSet<NSNetService> _services = [];
        public Dictionary<string, DiscoveredTrainer> Results { get; } = [];
        public TaskCompletionSource<string> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TrainerSearch(TimeSpan duration)
        {
            _browser.FoundService += (_, e) =>
            {
                if (!_services.Add(e.Service)) return;
                e.Service.AddressResolved += OnResolved;
                e.Service.Schedule(NSRunLoop.Main, NSRunLoopMode.Common);
                e.Service.Resolve(duration.TotalSeconds);
            };
            _browser.ServiceRemoved += (_, e) =>
            {
                Results.Remove(e.Service.Name);
                if (_services.TryGetValue(e.Service, out var service)) Release(service);
            };
            _browser.NotSearched += (_, e) => Failure.TrySetResult($"Bonjour: {e.Errors}");
            _browser.Schedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _browser.SearchForServices(TrainerService, Domain);
        }

        private void OnResolved(object? sender, EventArgs e)
        {
            if (sender is not NSNetService service) return;
            foreach (var address in service.Addresses ?? [])
            {
                var bytes = address.ToArray();
                // Darwin sockaddr_in stores sa_family at byte 1, with the IPv4 address at offset 4.
                if (bytes.Length < 8 || bytes[1] != 2) continue;
                var host = new IPAddress(bytes.AsSpan(4, 4)).ToString();
                var txt = new Dictionary<string, string>();
                if (service.TxtRecordData is { } record)
                {
                    using var entries = NSNetService.DictionaryFromTxtRecord(record);
                    foreach (var key in entries.Keys)
                        if (entries[key] is NSData value)
                            txt[key.ToString()] = Encoding.UTF8.GetString(value.ToArray());
                }
                Results[service.Name] = new DiscoveredTrainer(service.Name, host, (int)service.Port, txt);
                break;
            }
        }

        private void Release(NSNetService service)
        {
            _services.Remove(service);
            service.AddressResolved -= OnResolved;
            service.Stop();
            service.Unschedule(NSRunLoop.Main, NSRunLoopMode.Common);
            service.Dispose();
        }

        public void Dispose()
        {
            _browser.Stop();
            _browser.Unschedule(NSRunLoop.Main, NSRunLoopMode.Common);
            _browser.Dispose();
            foreach (var service in _services.ToArray()) Release(service);
        }
    }
}
#pragma warning restore CA1422
