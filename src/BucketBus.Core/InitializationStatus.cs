namespace BucketBus.Core;

/// <summary>Startup state shared by the shell and its diagnostics, with no provider data.</summary>
public sealed record InitializationStatus
{
    private InitializationStatus(InitializationPhase phase) => Phase = phase;

    public InitializationPhase Phase { get; }

    public static InitializationStatus Starting { get; } = new(InitializationPhase.Starting);

    public InitializationStatus Complete() => Phase switch
    {
        InitializationPhase.Starting => new(InitializationPhase.Ready),
        InitializationPhase.Ready => this,
        _ => throw new InvalidOperationException("Failed initialization cannot be reported as ready.")
    };

    public InitializationStatus Fail() => new(InitializationPhase.Failed);
}

public enum InitializationPhase
{
    Starting,
    Ready,
    Failed
}
