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
    private static readonly DevicePropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    // The level Settings > Bluetooth & devices shows, as a byte on the node that receives it:
    // the hands-free service of a classic headset, or the LE device itself.
    private static readonly DevicePropertyKey BatteryLevel = new(new("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
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

    // Battery percentage per physical device. The level sits on a sibling service node, not on an
    // ancestor of the audio endpoint, so it is matched through the Container ID the endpoint shares.
    public static Dictionary<Guid, int> ReadBatteryLevels()
    {
        var levels = new Dictionary<Guid, int>();
        foreach (var instanceId in PresentDeviceIds())
        {
            if (!IsBluetoothEnumerator(instanceId.Split('\\')[0]) || CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) continue;
            if (BatteryPercent(ReadProperty(node, BatteryLevel, 0x03)) is not int level) continue; // DEVPROP_TYPE_BYTE
            if (ReadProperty(node, ContainerId, 0x0d) is not { Length: 16 } container) continue; // DEVPROP_TYPE_GUID
            // Several services of one device may report; the lowest is the one about to run out.
            var key = new Guid(container);
            levels[key] = levels.TryGetValue(key, out var other) ? Math.Min(level, other) : level;
        }
        return levels;
    }

    internal static int? BatteryPercent(byte[]? value) => value is [var level] && level <= 100 ? level : null;

    private static string[] PresentDeviceIds()
    {
        const uint present = 0x100; // CM_GETIDLIST_FILTER_PRESENT
        // A device arriving between the two calls makes the list longer than measured; measure again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, null, present) != 0) break;
            var buffer = new char[length];
            var result = CM_Get_Device_ID_ListW(null, buffer, length, present);
            if (result == 0) return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != 0x1a) break; // CR_BUFFER_SMALL
        }
        return [];
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
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);
}
