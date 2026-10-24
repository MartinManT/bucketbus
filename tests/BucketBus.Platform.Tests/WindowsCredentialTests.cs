using System.Runtime.InteropServices;
using BucketBus.Core;
using static BucketBus.Platform.WindowsCredentialStore;

namespace BucketBus.Platform.Tests;

public sealed class WindowsCredentialTests
{
    private static CredentialReference Reference() => new(Guid.NewGuid(), Guid.NewGuid(), CredentialStorage.Secure,
        new ProfileSettings("Example", "https://storage.example.com", "us-east-1", AddressingStyle.Path).TargetFingerprint());
    [Theory]
    [InlineData(false, CredentialStatus.Found)]
    [InlineData(true, CredentialStatus.RetrievalFailure)]
    public async Task Read_frees_and_zeroes_native_blob_even_when_decoding_fails(bool malformed, CredentialStatus expected)
    {
        var api = new FakeApi { Malformed = malformed };
        var result = await new WindowsCredentialStore(api).ReadAsync(Reference(), false, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.True(api.Freed);
        Assert.True(api.ZeroedBeforeFree);
    }
    [Fact]
    public async Task Write_sets_current_user_generic_local_persistence_and_honors_blob_limit()
    {
        var api = new FakeApi(); var store = new WindowsCredentialStore(api); var reference = Reference();
        Assert.Equal(CredentialStatus.Success, await store.WriteAsync(reference, new("key", "secret"), false, TestContext.Current.CancellationToken));
        Assert.Equal(reference.VaultKey, api.Target);
        Assert.Equal(1u, api.Type);
        Assert.Equal(2u, api.Persist);
        api.Writes = 0;
        // JSON escaping and UTF-8 length, not character count, determine the native limit.
        Assert.Equal(CredentialStatus.StorageFailure, await store.WriteAsync(reference, new("key", new string('é', 1024)), false, TestContext.Current.CancellationToken));
        Assert.Equal(0, api.Writes);
    }
    [Fact]
    public async Task Cancellation_and_unavailable_platform_do_not_call_native_api()
    {
        var api = new FakeApi { IsAvailable = false };
        var store = new WindowsCredentialStore(api);
        Assert.Equal(CredentialStatus.Unavailable, (await store.ReadAsync(Reference(), false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(CredentialStatus.Cancelled, await store.DeleteAsync(Reference(), false, new(true)));
        Assert.Equal(0, api.Reads);
    }
    [Theory]
    [InlineData(1168, CredentialStatus.Missing, CredentialStatus.Missing)]
    [InlineData(5, CredentialStatus.Denied, CredentialStatus.Denied)]
    [InlineData(1312, CredentialStatus.Unavailable, CredentialStatus.Unavailable)]
    [InlineData(1223, CredentialStatus.Cancelled, CredentialStatus.Cancelled)]
    [InlineData(87, CredentialStatus.RetrievalFailure, CredentialStatus.StorageFailure)]
    public async Task Native_errors_remain_explicit_results_without_freeing_an_unowned_pointer(
        int error, CredentialStatus readStatus, CredentialStatus mutationStatus)
    {
        var api = new FakeApi { Error = error };
        var store = new WindowsCredentialStore(api);
        var reference = Reference();
        var read = await store.ReadAsync(reference, false, TestContext.Current.CancellationToken);
        Assert.Equal(readStatus, read.Status);
        Assert.Null(read.Value);
        Assert.False(api.Freed);
        Assert.Equal(mutationStatus, await store.WriteAsync(reference, new("key", "secret"), false,
            TestContext.Current.CancellationToken));
        Assert.Equal(mutationStatus, await store.DeleteAsync(reference, false, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void Credential_layout_matches_windows_x64_abi()
    {
        // This proves the managed layout on x64, not that Windows APIs were called.
        if (IntPtr.Size != 8) Assert.Skip("The published target is x64.");
        Assert.Equal(80, Marshal.SizeOf<NativeCredential>());
        Assert.Equal(32, Marshal.OffsetOf<NativeCredential>(nameof(NativeCredential.BlobSize)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<NativeCredential>(nameof(NativeCredential.Blob)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<NativeCredential>(nameof(NativeCredential.Persist)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<NativeCredential>(nameof(NativeCredential.UserName)).ToInt32());
    }
    private sealed class FakeApi : IWindowsCredentialApi
    {
        public bool IsAvailable { get; set; } = true;
        public bool Malformed, Freed, ZeroedBeforeFree;
        public int Reads, Writes;
        public int Error;
        public uint Type, Persist;
        public string? Target;
        public int Read(string target, out IntPtr pointer)
        {
            Reads++;
            if (Error != 0) { pointer = IntPtr.Zero; return Error; }
            var bytes = Malformed ? new byte[] { 0xff } : CredentialCodec.Encode(new("key", "secret"));
            var blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
            Marshal.StructureToPtr(new NativeCredential { Blob = blob, BlobSize = (uint)bytes.Length }, pointer, false);
            return 0;
        }
        public int Write(ref NativeCredential credential)
        { Writes++; Type = credential.Type; Persist = credential.Persist; Target = credential.TargetName; return Error; }
        public int Delete(string target) => Error != 0 ? Error : 1168;
        public void Free(IntPtr pointer)
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[native.BlobSize]; Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
            ZeroedBeforeFree = bytes.All(b => b == 0);
            Marshal.FreeHGlobal(native.Blob); Marshal.FreeHGlobal(pointer); Freed = true;
        }
    }
}
