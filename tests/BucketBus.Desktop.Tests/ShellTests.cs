using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using BucketBus.Desktop.Design;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BucketBus.Desktop.Tests;

public sealed class ShellTests
{
    [AvaloniaFact]
    public void Production_resources_and_injected_shell_initialize_headlessly()
    {
        Assert.IsType<App>(Application.Current);
        using var services = CompositionRoot.CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            Assert.True(window.IsVisible);
            Assert.NotNull(window.FindControl<Border>("LocalPane")!.BorderBrush);
            Assert.True(window.FindControl<Border>("LocalPane")!.Bounds.Width > 200);
            Assert.True(window.FindControl<ProgressBar>("StartupProgress")!.IsVisible);

            viewModel.CompleteInitialization();
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.FindControl<ProgressBar>("StartupProgress")!.IsVisible);
            Assert.False(window.FindControl<Border>("StartupError")!.IsVisible);
            Assert.Equal("Ready · Not connected", window.FindControl<TextBlock>("ShellStatus")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Pane_shortcuts_move_focus_and_target_the_active_location()
    {
        var window = new MainWindow { DataContext = DesignData.Preview };
        try
        {
            window.Show();
            Assert.Same(window.FindControl<Border>("LocalContent"), window.FocusManager!.GetFocusedElement());

            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Assert.Same(window.FindControl<Border>("RemoteContent"), window.FocusManager.GetFocusedElement());

            Press(window, PhysicalKey.L, RawInputModifiers.Control);
            Assert.Same(window.FindControl<TextBox>("RemoteLocation"), window.FocusManager.GetFocusedElement());

            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Press(window, PhysicalKey.L, RawInputModifiers.Control);
            Assert.Same(window.FindControl<TextBox>("LocalLocation"), window.FocusManager.GetFocusedElement());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Tab_and_shift_tab_provide_standard_focus_traversal()
    {
        var window = new MainWindow { DataContext = DesignData.Preview };
        try
        {
            window.Show();
            var location = window.FindControl<TextBox>("LocalLocation")!;
            location.Focus();

            Press(window, PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(window.FindControl<Border>("LocalContent"), window.FocusManager!.GetFocusedElement());

            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Same(location, window.FocusManager.GetFocusedElement());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Startup_failure_presents_safe_text_and_never_logs_the_raw_exception()
    {
        var recorder = new RecordingLoggerProvider();
        using var services = CompositionRoot.CreateServices(logging => logging.AddProvider(recorder));
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        const string sensitive = "synthetic-secret-do-not-log";
        try
        {
            window.Show();
            viewModel.ReportInitializationFailure(new InvalidOperationException(sensitive,
                new Exception("Authorization: " + sensitive)));
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.FindControl<Border>("StartupError")!.IsVisible);
            Assert.False(window.FindControl<ProgressBar>("StartupProgress")!.IsVisible);
            Assert.Equal("Startup failed · BB-STARTUP", window.FindControl<TextBlock>("ShellStatus")!.Text);
            Assert.Contains(recorder.Entries, entry => entry.Message.Contains("BB-STARTUP", StringComparison.Ordinal));
            Assert.All(recorder.Entries, entry =>
            {
                Assert.DoesNotContain(sensitive, entry.Message);
                Assert.Null(entry.Exception);
            });
            Assert.Throws<InvalidOperationException>(() => viewModel.CompleteInitialization());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Panes_have_accessible_names_and_editable_local_location()
    {
        var window = new MainWindow { DataContext = DesignData.Preview };
        try
        {
            window.Show();
            Assert.Equal("Local files pane", AutomationProperties.GetName(window.FindControl<Border>("LocalPane")!));
            Assert.Equal("Remote storage pane", AutomationProperties.GetName(window.FindControl<Border>("RemotePane")!));
            Assert.False(window.FindControl<TextBox>("LocalLocation")!.IsReadOnly);
            Assert.True(window.FindControl<TextBox>("RemoteLocation")!.IsReadOnly);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Design_data_uses_only_in_memory_safe_state()
    {
        var preview = DesignData.CreateViewModel();
        Assert.False(preview.IsLoading);
        Assert.False(preview.HasError);
        Assert.Equal("Ready · Not connected", preview.StatusMessage);
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
}
