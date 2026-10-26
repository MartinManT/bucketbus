using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using BucketBus.Core;

namespace BucketBus.Platform;

internal sealed class WindowsTransferFile : NativeTransferFile
{
    private readonly List<SafeFileHandle> _parents;
    private readonly string? _finalPath;
    private readonly Info _initial;
    private bool _committed, _disposed;
    private string? _temporaryName;
    public override FileStream Stream { get; }
    private WindowsTransferFile(List<SafeFileHandle> parents, SafeFileHandle file, string? finalPath)
    {
        _parents = parents; _finalPath = finalPath; _initial = Inspect(file);
        if ((_initial.Attributes & 0x410) != 0 || GetFileType(file) != 1) throw new TransferException(TransferError.InvalidName);
        Stream = new FileStream(file, finalPath is null ? FileAccess.Read : FileAccess.ReadWrite, 65536, false);
    }
    private static List<SafeFileHandle> PinDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\") || path.Length < 3 || path[1] != ':'
            || path[2] != '\\' || path.Contains('/') || path[2..].Split('\\').Any(p => p is "." or ".."))
            throw new TransferException(TransferError.InvalidName);
        var held = new List<SafeFileHandle>();
        try
        {
            var current = path[..3];
            foreach (var component in new[] { "" }.Concat(path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)))
            {
                if (component.Length != 0) { TransferNames.Validate(component); current = Path.Combine(current, component); }
                // Deny write/delete sharing to pin every ancestor and prevent reparse changes. OPEN_REPARSE_POINT prevents traversal.
                var handle = Open(current, 0x80, 1, 3, 0x02200000);
                held.Add(handle);
                if ((Inspect(handle).Attributes & 0x410) != 0x10) throw new TransferException(TransferError.InvalidName);
            }
            return held;
        }
        catch { foreach (var h in held) h.Dispose(); throw; }
    }
    internal static new WindowsTransferFile OpenSource(string path)
    {
        TransferNames.Validate(Path.GetFileName(path));
        var parents = PinDirectory(Path.GetDirectoryName(path) ?? "");
        SafeFileHandle? file = null;
        try { file = Open(path, 0x80000000, 1, 3, 0x00200000); return new(parents, file, null); }
        catch { file?.Dispose(); foreach (var h in parents) h.Dispose(); throw; }
    }
    internal static new WindowsTransferFile CreateDestination(string directory, string name)
    {
        var parents = PinDirectory(directory);
        SafeFileHandle? file = null;
        try
        {
            var temporaryName = ".bucketbus-" + Guid.NewGuid().ToString("N") + ".part";
            file = Open(Path.Combine(directory, temporaryName),
                0xc0010000, 0, 1, 0x00200100); // read/write/delete; no sharing; CREATE_NEW
            return new(parents, file, Path.Combine(directory, name)) { _temporaryName = temporaryName };
        }
        catch { file?.Dispose(); foreach (var h in parents) h.Dispose(); throw; }
    }
    public override TemporaryReceipt? Receipt => _temporaryName is null ? null
        : new("windows", _temporaryName, Identity(Inspect(_parents[^1])), "", Identity(_initial));
    private static string Identity(Info value) => $"{value.Volume}:{value.IndexHigh}:{value.IndexLow}:{value.Created.dwHighDateTime}:{value.Created.dwLowDateTime}";
    internal static void Recover(string directory, string name, TemporaryReceipt receipt)
    {
        var parents = PinDirectory(directory);
        try
        {
            if (Identity(Inspect(parents[^1])) != receipt.ParentIdentity) throw new TransferException(TransferError.RecoveryUnavailable);
            using (var final = CreateFileW(Path.Combine(directory, name), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (!final.IsInvalid) throw new TransferException(TransferError.Conflict);
                if (Marshal.GetLastPInvokeError() != 2) Fail();
            }
            using var file = CreateFileW(Path.Combine(directory, receipt.Name), 0x10080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (file.IsInvalid) { if (Marshal.GetLastPInvokeError() == 2) return; Fail(); }
            var info = Inspect(file);
            if ((info.Attributes & 0x410) != 0 || GetFileType(file) != 1 || Identity(info) != receipt.FileIdentity)
                throw new TransferException(TransferError.RecoveryUnavailable);
            var buffer = Marshal.AllocHGlobal(1);
            try { Marshal.WriteByte(buffer, 1); if (!SetFileInformationByHandle(file, 4, buffer, 1)) Fail(); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { foreach (var parent in parents) parent.Dispose(); }
    }
    public override void Validate()
    {
        var now = Inspect(Stream.SafeFileHandle);
        if (now.SizeHigh != _initial.SizeHigh || now.SizeLow != _initial.SizeLow || now.WriteTime != _initial.WriteTime
            || now.IndexHigh != _initial.IndexHigh || now.IndexLow != _initial.IndexLow || now.Volume != _initial.Volume)
            throw new TransferException(TransferError.SourceChanged);
    }
    public override void Commit()
    {
        var bytes = Encoding.Unicode.GetBytes(_finalPath!);
        var nameOffset = IntPtr.Size == 8 ? 20 : 12;
        var buffer = Marshal.AllocHGlobal(nameOffset + bytes.Length + 2);
        try
        {
            Marshal.Copy(new byte[nameOffset + bytes.Length + 2], 0, buffer, nameOffset + bytes.Length + 2);
            // ReplaceIfExists = FALSE; RootDirectory = NULL; absolute path held stable by ancestor handles.
            Marshal.WriteInt32(buffer, nameOffset - 4, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
            if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, buffer, (uint)(nameOffset + bytes.Length + 2))) Fail();
            _committed = true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public override void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_finalPath is not null && !_committed)
        {
            var buffer = Marshal.AllocHGlobal(1);
            try { Marshal.WriteByte(buffer, 1); CleanupRequired = !SetFileInformationByHandle(Stream.SafeFileHandle, 4, buffer, 1); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        Stream.Dispose(); foreach (var h in _parents) h.Dispose();
    }
    private static Info Inspect(SafeFileHandle file) { if (!GetFileInformationByHandle(file, out var info)) Fail(); return info; }
    private static SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags)
    { var handle = CreateFileW(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero); if (handle.IsInvalid) { handle.Dispose(); Fail(); } return handle; }
    private static void Fail() => throw new TransferException(Marshal.GetLastPInvokeError() switch
    { 80 or 183 => TransferError.Conflict, 5 or 32 => TransferError.PermissionDenied, 112 or 39 => TransferError.DiskFull,
      1 or 50 or 17 => TransferError.UnsupportedFilesystem, _ => TransferError.LocalFailure });
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct Info
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed;
        public long WriteTime; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, IntPtr info, uint size);
}
