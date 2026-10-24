using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop.Tests;

public sealed class ProfileEditorTests
{
    [AvaloniaFact]
    public async Task Editor_creates_profile_masks_and_clears_secrets_and_preserves_exact_prefix()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault();
        using var session = new SessionCredentialStore();
        var vm = new ProfileManagementViewModel(new(profiles, vault, session));
        var view = new ProfileManagementView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        try
        {
            window.Show();
            var name = view.FindControl<TextBox>("ProfileNameEditor")!;
            name.Focus(); Assert.Same(name, window.FocusManager!.GetFocusedElement());
            foreach (var control in new[] { "AccessKeyEditor", "SecretKeyEditor", "SessionTokenEditor" })
                Assert.NotEqual('\0', view.FindControl<TextBox>(control)!.PasswordChar);
            vm.ProfileName = "Example"; vm.Endpoint = "https://storage.example.com";
            vm.Prefix = "e\u0301//+%2F"; vm.CredentialChoice = CredentialChoice.SessionOnly;
            vm.AccessKey = "synthetic-key"; vm.SecretKey = "synthetic-secret"; vm.SessionToken = "synthetic-token";
            await vm.SaveCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var saved = Assert.Single(vm.Profiles);
            Assert.Equal("e\u0301//+%2F", saved.Settings.Prefix);
            Assert.Equal(CredentialStorage.Session, saved.Credential!.Storage);
            Assert.Equal("", vm.SecretKey); Assert.Equal("", vm.AccessKey); Assert.Equal("", vm.SessionToken);
            Assert.Equal(0, vault.Writes);
            vm.Selected = saved;
            await vm.CheckCredentialCommand.ExecuteAsync(null);
            Assert.Contains("Credential found", vm.Status);
            Assert.DoesNotContain("synthetic", vm.Status);
            Assert.Equal("", view.FindControl<TextBox>("SecretKeyEditor")!.Text);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Directional_profile_label_is_rejected_before_vault_or_metadata_write()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault(); using var memory = new SessionCredentialStore();
        var vm = new ProfileManagementViewModel(new(profiles, vault, memory))
        { ProfileName = "trusted\u202Etxt", Endpoint = "https://storage.example.com", CredentialChoice = CredentialChoice.SecureStore,
            AccessKey = "synthetic", SecretKey = "synthetic" };
        var view = new ProfileManagementView { DataContext = vm }; var window = new Window { Content = view };
        try
        {
            window.Show(); await vm.SaveCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.Empty(profiles.Snapshot.Profiles); Assert.Equal(0, vault.Writes);
            Assert.DoesNotContain('\u202E', view.FindControl<TextBlock>("ProfileStatus")!.Text ?? "");
            Assert.False(string.IsNullOrEmpty(vm.Status));
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task Temporary_token_secure_storage_is_rejected_without_vault_or_metadata_write()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault(); using var session = new SessionCredentialStore();
        var vm = new ProfileManagementViewModel(new(profiles, vault, session))
        { ProfileName = "Example", Endpoint = "https://storage.example.com", CredentialChoice = CredentialChoice.SecureStore,
            AccessKey = "key", SecretKey = "secret", SessionToken = "token" };
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("SessionOnly", vm.Status); Assert.Empty(profiles.Snapshot.Profiles); Assert.Equal(0, vault.Writes);
        Assert.Equal("", vm.SecretKey);
    }
    [AvaloniaFact]
    public async Task Loading_cancellation_and_error_are_visible_without_exposing_native_details()
    {
        var profiles = new FakeProfiles { Stall = true }; using var session = new SessionCredentialStore();
        var vm = new ProfileManagementViewModel(new(profiles, new FakeVault(), session));
        var view = new ProfileManagementView { DataContext = vm }; var window = new Window { Content = view };
        try
        {
            window.Show();
            var load = vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<ProgressBar>("ProfileProgress")!.IsVisible);
            vm.CancelCommand.Execute(null); await load; Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsBusy); Assert.Contains("cancelled", vm.Status);
            profiles.Stall = false; profiles.Throw = true;
            await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("BB-PROFILE", view.FindControl<TextBlock>("ProfileStatus")!.Text);
            Assert.DoesNotContain("synthetic-secret", vm.Status);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task Delete_requires_confirmation_and_locked_vault_reports_explicit_choice()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault(); using var session = new SessionCredentialStore();
        var vm = new ProfileManagementViewModel(new(profiles, vault, session))
        { ProfileName = "Example", Endpoint = "https://storage.example.com" };
        await vm.SaveCommand.ExecuteAsync(null);
        vm.Selected = Assert.Single(vm.Profiles);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Single(profiles.Snapshot.Profiles);
        vm.CredentialChoice = CredentialChoice.SecureStore; vm.AccessKey = "key"; vm.SecretKey = "secret";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("locked", vm.Status); Assert.Contains("SessionOnly", vm.Status);
        Assert.Single(profiles.Snapshot.Profiles);
        vm.ConfirmDelete = true; await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(profiles.Snapshot.Profiles);
    }
    private sealed class FakeProfiles : IProfileStore
    {
        public ProfileSnapshot Snapshot = new(0, []);
        public bool Stall, Throw;
        public async Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Throw) throw new IOException("synthetic-secret");
            return new(ProfileStatus.Success, Snapshot);
        }
        public Task<ProfileStatus> WriteAsync(ProfileSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        { Snapshot = snapshot; return Task.FromResult(ProfileStatus.Success); }
    }
    private sealed class FakeVault : ICredentialStore
    {
        public int Writes;
        public Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) => Task.FromResult(new CredentialResult(CredentialStatus.Locked));
        public Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default)
        { Writes++; return Task.FromResult(CredentialStatus.Locked); }
        public Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) => Task.FromResult(CredentialStatus.Missing);
    }
}
