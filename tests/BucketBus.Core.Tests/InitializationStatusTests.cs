using BucketBus.Core;

namespace BucketBus.Core.Tests;

public sealed class InitializationStatusTests
{
    [Fact]
    public void Failed_initialization_cannot_be_reported_as_ready()
    {
        var failed = InitializationStatus.Starting.Fail();

        Assert.Equal(InitializationPhase.Failed, failed.Phase);
        Assert.Throws<InvalidOperationException>(() => failed.Complete());
    }

    [Fact]
    public void Repeated_completion_does_not_create_a_new_startup()
    {
        var ready = InitializationStatus.Starting.Complete();

        Assert.Equal(InitializationPhase.Ready, ready.Phase);
        Assert.Same(ready, ready.Complete());
        Assert.Equal(InitializationPhase.Starting, InitializationStatus.Starting.Phase);
    }

    [Fact]
    public void Failure_after_startup_does_not_leave_a_ready_status()
    {
        var ready = InitializationStatus.Starting.Complete();

        Assert.Equal(InitializationPhase.Failed, ready.Fail().Phase);
        Assert.Equal(InitializationPhase.Ready, ready.Phase);
    }
}
