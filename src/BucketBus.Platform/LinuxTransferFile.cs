using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BucketBus.Core;

namespace BucketBus.Platform;

/// <summary>Handle-relative, no-follow operations. No path-based move/copy fallback.</summary>
internal sealed class LinuxTransferFile : NativeTransferFile
{
    private const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    private readonly SafeFileHandle _directory;
    private readonly SafeFileHandle? _temporaryDirectory;
    private readonly string _name;
    private readonly string? _temporaryName;
    private readonly Stat _initial;
    private readonly Stat _temporaryIdentity;
    private bool _disposed;
    public override FileStream Stream { get; }
    private LinuxTransferFile(SafeFileHandle directory, SafeFileHandle file, string name,
        SafeFileHandle? temporaryDirectory = null, string? temporaryName = null)
    {
        _directory = directory; _name = name; _temporaryDirectory = temporaryDirectory; _temporaryName = temporaryName;
        _initial = Info(file);
        if ((_initial.Mode & 0xf000) != 0x8000) throw new TransferException(TransferError.InvalidName);
        if (temporaryDirectory is not null) _temporaryIdentity = Info(temporaryDirectory);
        Stream = new FileStream(file, temporaryDirectory is null ? FileAccess.Read : FileAccess.ReadWrite, 65536, false);
    }
    private static SafeFileHandle OpenDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('/').Any(p => p is "." or "..") || path.Contains('\0'))
            throw new TransferException(TransferError.InvalidName);
        var handle = Handle(open("/", DirectoryFlag | NoFollow | CloseOnExec));
        try
        {
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Handle(openat(handle, component, DirectoryFlag | NoFollow | CloseOnExec, 0));
                handle.Dispose(); handle = next;
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static new LinuxTransferFile OpenSource(string path)
    {
        var parent = OpenDirectory(Path.GetDirectoryName(path) ?? "");
        SafeFileHandle? file = null;
        try
        {
            // O_NONBLOCK prevents a substituted FIFO from blocking before its type check.
            file = Handle(openat(parent, Path.GetFileName(path), NoFollow | CloseOnExec | 0x800, 0));
            return new(parent, file, Path.GetFileName(path));
        }
        catch { file?.Dispose(); parent.Dispose(); throw; }
    }
    internal static new LinuxTransferFile CreateDestination(string directory, string name)
    {
        var parent = OpenDirectory(directory);
        SafeFileHandle? temp = null, file = null;
        var unique = ".bucketbus-" + Guid.NewGuid().ToString("N") + ".tmp";
        var created = false;
        try
        {
            if (statx(parent, name, 0x100, 0x7ff, out _) == 0) throw new TransferException(TransferError.Conflict);
            if (Marshal.GetLastPInvokeError() != 2) Fail();
            if (mkdirat(parent, unique, 0x1c0) != 0) Fail(); // 0700, exclusive randomized directory
            created = true;
            temp = Handle(openat(parent, unique, DirectoryFlag | NoFollow | CloseOnExec, 0));
            file = Handle(openat(temp, "payload.part", 2 | 0x40 | 0x80 | NoFollow | CloseOnExec, 0x180)); // O_RDWR|CREAT|EXCL, 0600
            return new(parent, file, name, temp, unique);
        }
        catch
        {
            file?.Dispose();
            if (temp is not null) { unlinkat(temp, "payload.part", 0); temp.Dispose(); }
            if (created) unlinkat(parent, unique, 0x200);
            parent.Dispose(); throw;
        }
    }
    public override TemporaryReceipt? Receipt
    {
        get
        {
            var parent = Info(_directory);
            if (_temporaryDirectory is null || (parent.Mask & _initial.Mask & _temporaryIdentity.Mask & 0x800) == 0) return null;
            return new("linux", _temporaryName!, Identity(parent), Identity(_temporaryIdentity), Identity(_initial));
        }
    }
    private static string Identity(Stat value) => $"{value.DeviceMajor}:{value.DeviceMinor}:{value.Inode}:{value.BirthSeconds}:{value.BirthNanoseconds}";
    internal static void Recover(string directory, string name, TemporaryReceipt receipt)
    {
        using var parent = OpenDirectory(directory);
        if (Identity(Info(parent)) != receipt.ParentIdentity) throw new TransferException(TransferError.RecoveryUnavailable);
        if (statx(parent, name, 0x100, 0xfff, out _) == 0) throw new TransferException(TransferError.Conflict);
        if (Marshal.GetLastPInvokeError() != 2) Fail();
        if (statx(parent, receipt.Name, 0x100, 0xfff, out var container) != 0)
        { if (Marshal.GetLastPInvokeError() == 2) return; Fail(); }
        using var temp = Handle(openat(parent, receipt.Name, DirectoryFlag | NoFollow | CloseOnExec, 0));
        if (Identity(Info(temp)) != receipt.ContainerIdentity || Identity(container) != receipt.ContainerIdentity)
            throw new TransferException(TransferError.RecoveryUnavailable);
        if (statx(temp, "payload.part", 0x100, 0xfff, out var payload) == 0)
        {
            if ((payload.Mode & 0xf000) != 0x8000 || Identity(payload) != receipt.FileIdentity)
                throw new TransferException(TransferError.RecoveryUnavailable);
            if (unlinkat(temp, "payload.part", 0) != 0) Fail();
        }
        else if (Marshal.GetLastPInvokeError() != 2) Fail();
        if (statx(parent, receipt.Name, 0x100, 0xfff, out container) != 0 || Identity(container) != receipt.ContainerIdentity)
            throw new TransferException(TransferError.RecoveryUnavailable);
        if (unlinkat(parent, receipt.Name, 0x200) != 0) Fail();
    }
    public override void Validate()
    {
        var now = Info(Stream.SafeFileHandle);
        using var selected = Handle(openat(_directory, _name, NoFollow | CloseOnExec | 0x800, 0));
        var path = Info(selected);
        if (now != _initial || !SameIdentity(now, path)) throw new TransferException(TransferError.SourceChanged);
    }
    public override void Commit()
    {
        // Link the actual open inode, never a possibly substituted temporary pathname.
        // linkat is atomic no-replace (also rejects an existing symlink). Unsupported filesystems fail closed.
        var descriptorPath = "/proc/self/fd/" + Stream.SafeFileHandle.DangerousGetHandle().ToInt64();
        if (linkat(-100, descriptorPath, _directory, _name, 0x400) != 0) Fail();
    }
    public override void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_temporaryDirectory is not null)
        {
            if (statx(_temporaryDirectory, "payload.part", 0x100, 0x7ff, out var file) == 0 && SameIdentity(_initial, file))
                CleanupRequired |= unlinkat(_temporaryDirectory, "payload.part", 0) != 0;
            else CleanupRequired = true;
            if (statx(_directory, _temporaryName!, 0x100, 0x7ff, out var directory) == 0 && SameIdentity(_temporaryIdentity, directory))
                CleanupRequired |= unlinkat(_directory, _temporaryName!, 0x200) != 0;
            else CleanupRequired = true;
            _temporaryDirectory.Dispose();
        }
        Stream.Dispose(); _directory.Dispose();
    }
    private static bool SameIdentity(Stat a, Stat b) => a.Inode == b.Inode && a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;
    private static Stat Info(SafeFileHandle file)
    {
        if (statx(file, "", 0x1000, 0xfff, out var info) != 0) Fail();
        if ((info.Mask & 0x3c3) != 0x3c3) throw new TransferException(TransferError.UnsupportedFilesystem);
        return info;
    }
    private static SafeFileHandle Handle(int fd) { if (fd < 0) Fail(); return new((IntPtr)fd, true); }
    private static void Fail() => throw new TransferException(Marshal.GetLastPInvokeError() switch
    { 17 => TransferError.Conflict, 1 or 13 or 30 => TransferError.PermissionDenied, 28 or 122 => TransferError.DiskFull,
      38 or 95 or 18 => TransferError.UnsupportedFilesystem, 40 or 20 => TransferError.InvalidName, _ => TransferError.LocalFailure });
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Stat : IEquatable<Stat>
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        public bool Equals(Stat b) => SameIdentity(this, b) && Mode == b.Mode && Size == b.Size && ChangeSeconds == b.ChangeSeconds
            && ChangeNanoseconds == b.ChangeNanoseconds && ModifiedSeconds == b.ModifiedSeconds && ModifiedNanoseconds == b.ModifiedNanoseconds;
        public override bool Equals(object? obj) => obj is Stat b && Equals(b);
        public override int GetHashCode() => Inode.GetHashCode();
        public static bool operator ==(Stat a, Stat b) => a.Equals(b);
        public static bool operator !=(Stat a, Stat b) => !a.Equals(b);
    }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(SafeFileHandle directory, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int mkdirat(SafeFileHandle directory, string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(SafeFileHandle directory, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int linkat(int oldDirectory, string oldPath, SafeFileHandle directory, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int statx(SafeFileHandle directory, string path, int flags, uint mask, out Stat info);
}
