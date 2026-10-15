using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unbounded.Panorama.Transport;

/// <summary>
/// Overlapped ReadFile/WriteFile on a device handle opened with FILE_FLAG_OVERLAPPED,
/// completed through the thread-pool I/O completion port. Reads and writes may be
/// outstanding at the same time.
/// </summary>
internal sealed unsafe class OverlappedDevice : IDisposable
{
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;

    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _bound;

    public OverlappedDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _bound = ThreadPoolBoundHandle.BindHandle(handle);
    }

    public Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken) =>
        IoAsync(write: false, buffer, buffer.Length, cancellationToken);

    public Task<int> WriteAsync(byte[] buffer, CancellationToken cancellationToken) =>
        IoAsync(write: true, buffer, buffer.Length, cancellationToken);

    public void CancelAll() => CancelIoEx(_handle, null);

    private Task<int> IoAsync(bool write, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var op = new PendingIo(this, write);
        // pinData keeps the buffer pinned until the overlapped is freed.
        op.Overlapped = _bound.AllocateNativeOverlapped(OnComplete, op, buffer);

        bool ok;
        fixed (byte* p = buffer)
        {
            ok = write
                ? WriteFile(_handle, p, count, null, op.Overlapped)
                : ReadFile(_handle, p, count, null, op.Overlapped);
        }

        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_IO_PENDING)
            {
                // Failed synchronously: no completion packet will be queued.
                op.Free();
                return Task.FromException<int>(new Win32Exception(error, $"{(write ? "WriteFile" : "ReadFile")} failed: Win32 {error}"));
            }
        }
        // On synchronous success a completion packet is still queued, so OnComplete runs either way.

        if (cancellationToken.CanBeCanceled)
            op.Registration = cancellationToken.UnsafeRegister(static s => ((PendingIo)s!).Cancel(), op);
        return op.Completion.Task;
    }

    private static readonly IOCompletionCallback OnComplete = (errorCode, bytes, overlapped) =>
    {
        var op = (PendingIo)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        op.Complete(errorCode, bytes);
    };

    private sealed class PendingIo(OverlappedDevice owner, bool write)
    {
        private readonly object _gate = new();
        private bool _done;

        public NativeOverlapped* Overlapped;
        public CancellationTokenRegistration Registration;
        public readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_done)
                    CancelIoEx(owner._handle, Overlapped);
            }
        }

        public void Complete(uint errorCode, uint bytes)
        {
            Free();
            Registration.Dispose();
            if (errorCode == 0)
                Completion.TrySetResult((int)bytes);
            else if (errorCode == ERROR_OPERATION_ABORTED)
                Completion.TrySetCanceled();
            else
                Completion.TrySetException(new Win32Exception((int)errorCode,
                    $"{(write ? "Write" : "Read")} completed with Win32 {errorCode}: {new Win32Exception((int)errorCode).Message}"));
        }

        public void Free()
        {
            lock (_gate)
            {
                if (_done)
                    return;
                _done = true;
                owner._bound.FreeNativeOverlapped(Overlapped);
            }
        }
    }

    public void Dispose() => _bound.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int count, int* read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte* buffer, int count, int* written, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);
}
