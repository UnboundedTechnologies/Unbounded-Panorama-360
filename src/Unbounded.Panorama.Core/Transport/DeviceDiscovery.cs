using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Unbounded.Panorama.Transport;

public sealed record PanoramaDeviceInfo(string InterfacePath, ushort VendorId, ushort ProductId, string Serial)
{
    public string ProductName => ProductId switch
    {
        0x1011 => "Panorama",
        0x1021 => "Panorama SE",
        0x1031 => "Panorama WB",
        _ => $"Unknown 391a:{ProductId:x4}",
    };
}

/// <summary>
/// Finds TRYX displays through the Windows USB printer device interface
/// (GUID_DEVINTERFACE_USBPRINT), which the in-box usbprint.sys driver registers.
/// </summary>
public static partial class DeviceDiscovery
{
    public const ushort TryxVendorId = 0x391a;
    public static readonly ushort[] SupportedProductIds = [0x1011, 0x1021, 0x1031];

    private static readonly Guid UsbPrintInterface = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    public static IReadOnlyList<PanoramaDeviceInfo> Enumerate()
    {
        var result = new List<PanoramaDeviceInfo>();
        foreach (var path in GetInterfacePaths(UsbPrintInterface))
        {
            var match = VidPidRegex().Match(path);
            if (!match.Success)
                continue;
            var vid = Convert.ToUInt16(match.Groups["vid"].Value, 16);
            var pid = Convert.ToUInt16(match.Groups["pid"].Value, 16);
            if (vid != TryxVendorId || !SupportedProductIds.Contains(pid))
                continue;
            result.Add(new PanoramaDeviceInfo(path, vid, pid, match.Groups["serial"].Value));
        }
        return result;
    }

    private static List<string> GetInterfacePaths(Guid interfaceClass)
    {
        const int CR_SUCCESS = 0;
        const int CR_BUFFER_SMALL = 0x1A;
        const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

        while (true)
        {
            var cr = CM_Get_Device_Interface_List_SizeW(out var length, ref interfaceClass, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr != CR_SUCCESS)
                throw new InvalidOperationException($"CM_Get_Device_Interface_List_Size failed: CONFIGRET 0x{cr:x}");

            var buffer = new char[length];
            cr = CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr == CR_BUFFER_SMALL)
                continue; // a device arrived between the two calls
            if (cr != CR_SUCCESS)
                throw new InvalidOperationException($"CM_Get_Device_Interface_List failed: CONFIGRET 0x{cr:x}");

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    [GeneratedRegex(@"vid_(?<vid>[0-9a-f]{4})&pid_(?<pid>[0-9a-f]{4})#(?<serial>[^#]*)#", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLength, uint flags);
}
