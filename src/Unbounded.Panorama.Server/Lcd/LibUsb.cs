using System.Runtime.InteropServices;

namespace Unbounded.Panorama.Server.Lcd;

/// <summary>The few libusb-1.0 calls the screen drivers need (raw bulk transfers on Linux).</summary>
internal static partial class LibUsb
{
    private const string Library = "libusb-1.0.so.0";

    public const int Success = 0;
    public const int ErrorTimeout = -7;
    public const int ErrorNoDevice = -4;

    [LibraryImport(Library, EntryPoint = "libusb_init")]
    public static partial int Init(out IntPtr context);

    [LibraryImport(Library, EntryPoint = "libusb_exit")]
    public static partial void Exit(IntPtr context);

    [LibraryImport(Library, EntryPoint = "libusb_open_device_with_vid_pid")]
    public static partial IntPtr OpenDeviceWithVidPid(IntPtr context, ushort vendorId, ushort productId);

    [LibraryImport(Library, EntryPoint = "libusb_close")]
    public static partial void Close(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "libusb_set_auto_detach_kernel_driver")]
    public static partial int SetAutoDetachKernelDriver(IntPtr handle, int enable);

    [LibraryImport(Library, EntryPoint = "libusb_claim_interface")]
    public static partial int ClaimInterface(IntPtr handle, int interfaceNumber);

    [LibraryImport(Library, EntryPoint = "libusb_release_interface")]
    public static partial int ReleaseInterface(IntPtr handle, int interfaceNumber);

    [LibraryImport(Library, EntryPoint = "libusb_clear_halt")]
    public static partial int ClearHalt(IntPtr handle, byte endpoint);

    [LibraryImport(Library, EntryPoint = "libusb_bulk_transfer")]
    public static unsafe partial int BulkTransfer(IntPtr handle, byte endpoint, byte* data, int length, out int transferred, uint timeoutMs);

    [LibraryImport(Library, EntryPoint = "libusb_error_name")]
    private static partial IntPtr ErrorNamePtr(int code);

    public static string ErrorName(int code) => Marshal.PtrToStringAnsi(ErrorNamePtr(code)) ?? code.ToString();
}
