using System.Runtime.InteropServices;
using System.Text;

namespace AudioFromWhatDevice;

internal static class BluetoothDeviceDetector
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DevicePropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    private static readonly DevicePropertyKey EnumeratorName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly DevicePropertyKey ClassGuid = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 10);
    private static readonly Guid BluetoothClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    // This takes a PnP enumerator property, never a user-editable device name or endpoint ID.
    internal static bool IsBluetoothEnumerator(string? name) =>
        name?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsBluetoothDevice(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId) || CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return false;
        var visited = new HashSet<uint>();
        // Audio endpoints start at SWD; inspect their ancestors for classic/HFP/LE Bluetooth.
        // Continue past USB parents: the Bluetooth radio itself can be USB-connected.
        for (var depth = 0; depth < 24 && visited.Add(node); depth++)
        {
            var enumerator = ReadProperty(node, EnumeratorName, 0x12); // DEVPROP_TYPE_STRING
            if (enumerator is not null && IsBluetoothEnumerator(Encoding.Unicode.GetString(enumerator).TrimEnd('\0')))
                return true;
            var classBytes = ReadProperty(node, ClassGuid, 0x0d); // DEVPROP_TYPE_GUID
            if (classBytes is { Length: 16 } && new Guid(classBytes) == BluetoothClass) return true;
            if (CM_Get_Parent(out var parent, node, 0) != 0) break;
            node = parent;
        }
        return false;
    }

    private static byte[]? ReadProperty(uint node, DevicePropertyKey key, uint expectedType)
    {
        var buffer = new byte[512];
        uint size = (uint)buffer.Length;
        var result = CM_Get_DevNode_PropertyW(node, ref key, out var type, buffer, ref size, 0);
        return result == 0 && type == expectedType && size <= buffer.Length ? buffer[..(int)size] : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref DevicePropertyKey key,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
