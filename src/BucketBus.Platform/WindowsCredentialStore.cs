using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BucketBus.Core;

namespace BucketBus.Platform;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private readonly IWindowsCredentialApi _api;
    public WindowsCredentialStore() : this(new WindowsCredentialApi()) { }
    internal WindowsCredentialStore(IWindowsCredentialApi api) => _api = api;
    private const uint Generic = 1, LocalMachine = 2;
    private const int MaximumBlobBytes = 2560;
    public Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var guard = Guard(reference, cancellationToken);
        if (guard is not null) return new CredentialResult(guard.Value);
        var error = _api.Read(reference.VaultKey, out var pointer);
        if (error != 0) return new CredentialResult(MapError(error, false));
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.BlobSize is 0 or > MaximumBlobBytes || native.Blob == IntPtr.Zero)
                return new CredentialResult(CredentialStatus.RetrievalFailure);
            var bytes = new byte[native.BlobSize];
            Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
            try { return new CredentialResult(CredentialStatus.Found, CredentialCodec.Decode(bytes)); }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException) { return new CredentialResult(CredentialStatus.RetrievalFailure); }
        }
        finally
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.Blob != IntPtr.Zero && native.BlobSize <= MaximumBlobBytes)
                for (var i = 0; i < native.BlobSize; i++) Marshal.WriteByte(native.Blob, i, 0);
            _api.Free(pointer);
        }
    });
    public Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (credentials.SessionToken is not null) return CredentialStatus.Denied;
        var guard = Guard(reference, cancellationToken);
        if (guard is not null) return guard.Value;
        var bytes = CredentialCodec.Encode(credentials);
        IntPtr blob = IntPtr.Zero;
        try
        {
            if (bytes.Length > MaximumBlobBytes) return CredentialStatus.StorageFailure;
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential { Type = Generic, TargetName = reference.VaultKey,
                BlobSize = (uint)bytes.Length, Blob = blob, Persist = LocalMachine, UserName = "BucketBus" };
            var error = _api.Write(ref native);
            return error == 0 ? CredentialStatus.Success : MapError(error, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (blob != IntPtr.Zero)
            {
                for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }
    });
    public Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var guard = Guard(reference, cancellationToken);
        if (guard is not null) return guard.Value;
        var error = _api.Delete(reference.VaultKey);
        return error == 0 ? CredentialStatus.Success : MapError(error, true);
    });
    private CredentialStatus? Guard(CredentialReference reference, CancellationToken token) => token.IsCancellationRequested
        ? CredentialStatus.Cancelled : !reference.IsValid || reference.Storage != CredentialStorage.Secure ? CredentialStatus.Denied
        : !_api.IsAvailable ? CredentialStatus.Unavailable : null;
    internal static CredentialStatus MapError(int error, bool write) => error switch
    {
        1168 => CredentialStatus.Missing, 5 => CredentialStatus.Denied, 1312 => CredentialStatus.Unavailable,
        1223 => CredentialStatus.Cancelled, _ => write ? CredentialStatus.StorageFailure : CredentialStatus.RetrievalFailure
    };
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeCredential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    internal interface IWindowsCredentialApi
    {
        bool IsAvailable { get; }
        int Read(string target, out IntPtr pointer);
        int Write(ref NativeCredential credential);
        int Delete(string target);
        void Free(IntPtr pointer);
    }
    private sealed class WindowsCredentialApi : IWindowsCredentialApi
    {
        public bool IsAvailable => OperatingSystem.IsWindows();
        public int Read(string target, out IntPtr pointer) => CredRead(target, Generic, 0, out pointer) ? 0 : Marshal.GetLastPInvokeError();
        public int Write(ref NativeCredential credential) => CredWrite(ref credential, 0) ? 0 : Marshal.GetLastPInvokeError();
        public int Delete(string target) => CredDelete(target, Generic, 0) ? 0 : Marshal.GetLastPInvokeError();
        public void Free(IntPtr pointer) => CredFree(pointer);
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
