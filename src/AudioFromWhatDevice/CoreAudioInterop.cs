using System.Runtime.InteropServices;

namespace AudioFromWhatDevice;

internal static class Native
{
    public static readonly PropertyKey FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    public static readonly PropertyKey EnumeratorName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    public static readonly PropertyKey InstanceId = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    public static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    // RCWs are privately owned and only accessed/released on the monitor thread.
    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    public static T Activate<T>(IMMDevice device) where T : class
    {
        var iid = typeof(T).GUID;
        Check(device.Activate(ref iid, 23, IntPtr.Zero, out var value)); // CLSCTX_ALL
        return (T)value;
    }

    public static string? ReadProperty(IMMDevice device, PropertyKey key)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            Check(device.OpenPropertyStore(0, out store)); // STGM_READ
            Check(store.GetValue(ref key, out value));
            return value.Type switch
            {
                31 when value.Pointer != IntPtr.Zero => Marshal.PtrToStringUni(value.Pointer), // VT_LPWSTR
                72 when value.Pointer != IntPtr.Zero => Marshal.PtrToStructure<Guid>(value.Pointer).ToString(),
                _ => null
            };
        }
        finally
        {
            PropVariantClear(ref value);
            Release(store);
        }
    }

    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr window);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

// The project targets 64-bit Windows; native PROPVARIANT is 24 bytes on x64/ARM64.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Pointer;
}

[ComImport, Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
internal class MMDeviceEnumeratorCom { }

[ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient callback);
}

[ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    // The LPWStr marshaler frees the returned CoTaskMem string.
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("c02216f6-8c67-4b5b-9d00-d008e73e0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
    [PreserveSig] int GetMeteringChannelCount(out uint count);
    [PreserveSig] int GetChannelsPeakValues(uint count, IntPtr peaks);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
}

[ComImport, Guid("5cdf2c82-841e-4546-9722-0cf74078229a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
    [PreserveSig] int VolumeStepUp(ref Guid context);
    [PreserveSig] int VolumeStepDown(ref Guid context);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
}

[ComImport, Guid("7991eec9-7e89-4d85-8390-6c703cec60c0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceNotifications : IMMNotificationClient
{
    private int dirty = 1;
    internal bool TakeChange() => Interlocked.Exchange(ref dirty, 0) != 0;
    private int Mark() { Interlocked.Exchange(ref dirty, 1); return 0; }
    // No COM calls, UI calls, waits, or reference release inside these callbacks.
    public int OnDeviceStateChanged(string id, uint state) => Mark();
    public int OnDeviceAdded(string id) => Mark();
    public int OnDeviceRemoved(string id) => Mark();
    public int OnDefaultDeviceChanged(int flow, int role, string? id) => Mark();
    public int OnPropertyValueChanged(string id, PropertyKey key) => Mark();
}
