using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop.ViewModels;

// Only display preferences live here; no transfer, vault or journal dependencies.
public sealed class DisplayOptions : ObservableObject
{
    private DisplayPreferences _current = new();
    public DisplayPreferences Current { get => _current; set { if (value.IsValid) SetProperty(ref _current, value); } }
}
public sealed class SettingsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPreferencesStore _store;
    private bool _saving, _initialized;
    private long _edits;
    private string _status = "Changes apply immediately. Save to retain them for the next launch.";
    public DisplayOptions Display { get; }
    public IReadOnlyList<FileSizeDisplay> FileSizeOptions { get; } = Enum.GetValues<FileSizeDisplay>();
    public IReadOnlyList<RemoteNameDisplay> RemoteNameOptions { get; } = Enum.GetValues<RemoteNameDisplay>();
    public FileSizeDisplay FileSizes { get => Display.Current.FileSizes; set { if (Enum.IsDefined(value)) Display.Current = Display.Current with { FileSizes = value }; } }
    public RemoteNameDisplay RemoteNames { get => Display.Current.RemoteNames; set { if (Enum.IsDefined(value)) Display.Current = Display.Current with { RemoteNames = value }; } }
    public bool IsSaving { get => _saving; private set { SetProperty(ref _saving, value); SaveCommand.NotifyCanExecuteChanged(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IAsyncRelayCommand SaveCommand { get; }
    public SettingsViewModel(IPreferencesStore store, DisplayOptions display)
    {
        _store = store; Display = display;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
        Display.PropertyChanged += (_, _) =>
        {
            _edits++; OnPropertyChanged(nameof(FileSizes)); OnPropertyChanged(nameof(RemoteNames));
            Status = "Applied for this session. Save to retain these preferences.";
        };
    }
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true; var edits = _edits;
        try
        {
            var result = await _store.ReadAsync(CancellationToken.None);
            if (_edits != edits) return; // Startup must not overwrite a user's new selection.
            Display.Current = result.Value;
            Status = result.Status switch
            {
                PreferencesReadStatus.Loaded => "Saved preferences loaded.",
                PreferencesReadStatus.Missing => "Using default preferences. Changes apply immediately; Save retains them.",
                _ => "Preferences could not be read. Using defaults; browsing and transfers remain available."
            };
        }
        catch { if (_edits == edits) Status = "Preferences could not be read. Using defaults; browsing and transfers remain available."; }
    }
    public async ValueTask DisposeAsync() { try { await (SaveCommand.ExecutionTask ?? Task.CompletedTask).ConfigureAwait(false); } catch { } }
    private async Task SaveAsync()
    {
        var snapshot = Display.Current; IsSaving = true;
        try
        {
            var saved = await _store.SaveAsync(snapshot, CancellationToken.None);
            Status = !saved ? "Could not save preferences. Your choices apply to this session only. Retry Save."
                : snapshot == Display.Current ? "Preferences saved." : "Earlier choices saved. Save again to retain your latest changes.";
        }
        catch { Status = "Could not save preferences. Your choices apply to this session only. Retry Save."; }
        finally { IsSaving = false; }
    }
}
