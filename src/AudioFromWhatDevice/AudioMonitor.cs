using System.Runtime.InteropServices;

namespace AudioFromWhatDevice;

internal sealed class AudioMonitor : IDisposable
{
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread worker;
    private int disposed;
    private MonitorSnapshot latest = MonitorSnapshot.Initial;
    public MonitorSnapshot Latest => Volatile.Read(ref latest);

    public AudioMonitor()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "Core Audio monitor" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void Publish(EndpointSnapshot[] devices, string? error = null, string? defaultOutputId = null) =>
        Volatile.Write(ref latest, new MonitorSnapshot(devices, error, DateTimeOffset.Now) { DefaultOutputId = defaultOutputId });

    private void Run()
    {
        var initialized = false;
        try
        {
            Native.Check(Native.CoInitializeEx(IntPtr.Zero, 0)); // COINIT_MULTITHREADED
            initialized = true;
            while (!stop.WaitOne(0))
            {
                try { MonitorUntilFailure(); }
                catch (Exception ex) { Publish([], $"音频服务暂不可用：{ex.Message}"); }
                if (stop.WaitOne(1500)) break;
            }
        }
        catch (Exception ex) { Publish([], $"无法初始化音频监测：{ex.Message}"); }
        finally { if (initialized) Native.CoUninitialize(); }
    }

    private void MonitorUntilFailure()
    {
        IMMDeviceEnumerator? enumerator = null;
        var notifications = new DeviceNotifications();
        var endpoints = new List<Endpoint>();
        var registered = false;
        long nextRefresh = 0;
        string? defaultOutputId = null;
        try
        {
            enumerator = Native.CreateEnumerator();
            Native.Check(enumerator.RegisterEndpointNotificationCallback(notifications));
            registered = true;
            do
            {
                var now = Environment.TickCount64;
                if (notifications.TakeChange() || now >= nextRefresh)
                {
                    var replacement = Enumerate(enumerator, endpoints);
                    foreach (var endpoint in endpoints) endpoint.Dispose();
                    endpoints = replacement;
                    defaultOutputId = GetDefaultOutputId(enumerator);
                    nextRefresh = now + 3000; // Also recovers missed driver notifications.
                }
                Publish(endpoints.Select(endpoint => endpoint.Sample(now)).ToArray(), defaultOutputId: defaultOutputId);
            } while (!stop.WaitOne(50));
        }
        finally
        {
            if (registered) enumerator!.UnregisterEndpointNotificationCallback(notifications);
            foreach (var endpoint in endpoints) endpoint.Dispose();
            Native.Release(enumerator);
            GC.KeepAlive(notifications);
        }
    }

    private static string? GetDefaultOutputId(IMMDeviceEnumerator enumerator)
    {
        // Multimedia is the normal playback role; fall back if that role has no endpoint.
        foreach (var role in new[] { 1, 0, 2 }) // eMultimedia, eConsole, eCommunications
        {
            IMMDevice? device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, role, out device) >= 0 &&
                    device.GetId(out var id) >= 0) return id;
            }
            finally { Native.Release(device); }
        }
        return null;
    }

    private static List<Endpoint> Enumerate(IMMDeviceEnumerator enumerator, List<Endpoint> previous)
    {
        var states = previous.ToDictionary(endpoint => endpoint.Id, endpoint => endpoint.State, StringComparer.Ordinal);
        var result = new List<Endpoint>();
        IMMDeviceCollection? collection = null;
        try
        {
            // eRender = 0; DEVICE_STATE_ACTIVE = 1 means available, NOT currently playing.
            Native.Check(enumerator.EnumAudioEndpoints(0, 1, out collection));
            Native.Check(collection.GetCount(out var count));
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Native.Check(collection.Item(index, out device));
                    Native.Check(device.GetId(out var id));
                    var state = states.GetValueOrDefault(id) ?? new ActivityState();
                    result.Add(new Endpoint(device, id, state));
                    device = null; // Ownership transferred.
                }
                finally { Native.Release(device); }
            }
            return result;
        }
        catch
        {
            foreach (var endpoint in result) endpoint.Dispose();
            throw;
        }
        finally { Native.Release(collection); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        // A broken driver must not freeze UI shutdown indefinitely.
        if (worker.Join(2000)) stop.Dispose();
    }

    private sealed class Endpoint : IDisposable
    {
        private readonly IMMDevice device;
        private IAudioMeterInformation? meter;
        private IAudioEndpointVolume? volume;
        private string? initializationError;
        private bool hardwareMeter;
        public string Id { get; }
        public ActivityState State { get; }
        private readonly string name;
        private readonly string? containerId;
        private readonly bool isBluetooth;

        public Endpoint(IMMDevice device, string id, ActivityState state)
        {
            this.device = device;
            Id = id;
            State = state;
            name = SafeProperty(Native.FriendlyName) ?? "未命名输出设备";
            containerId = SafeProperty(Native.ContainerId);
            // Endpoint stores expose the adapter enumerator even when InstanceId is absent.
            isBluetooth = BluetoothDeviceDetector.IsBluetoothEnumerator(SafeProperty(Native.EnumeratorName)) ||
                BluetoothDeviceDetector.IsBluetoothDevice(SafeProperty(Native.InstanceId));
            try
            {
                meter = Native.Activate<IAudioMeterInformation>(device);
                volume = Native.Activate<IAudioEndpointVolume>(device);
                Native.Check(meter.QueryHardwareSupport(out var mask));
                hardwareMeter = (mask & 4) != 0; // ENDPOINT_HARDWARE_SUPPORT_METER
            }
            catch (Exception ex) { initializationError = ex.Message; }
        }

        private string? SafeProperty(PropertyKey key)
        {
            try { return Native.ReadProperty(device, key); }
            catch (COMException) { return null; }
        }

        public EndpointSnapshot Sample(long now)
        {
            try
            {
                Native.Check(device.GetState(out var state));
                if (state != 1) return Unavailable("设备已断开或不可用，正在刷新。", isAvailable: false);
                if (initializationError is not null) return Unavailable(initializationError);
                Native.Check(meter!.GetPeakValue(out var peak));
                Native.Check(volume!.GetMute(out var muted));
                Native.Check(volume.GetMasterVolumeLevelScalar(out var level));
                return new(Id, name, containerId, peak, level, muted, hardwareMeter,
                    State.Update(peak, muted, level, now), null) { IsBluetooth = isBluetooth };
            }
            catch (Exception ex) { return Unavailable(ex.Message); }
        }

        private EndpointSnapshot Unavailable(string error, bool isAvailable = true)
        {
            State.Update(0, true, 0, Environment.TickCount64); // Clear stale activity immediately.
            return new(Id, name, containerId, 0, 0, false, hardwareMeter, Activity.Unavailable, error) { IsBluetooth = isBluetooth, IsAvailable = isAvailable };
        }

        public void Dispose()
        {
            Native.Release(volume);
            Native.Release(meter);
            Native.Release(device);
        }
    }
}
