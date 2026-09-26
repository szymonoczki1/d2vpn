namespace D2Vpn.Cli;

/// <summary>Runs an action on dispose, for unhooking an event with <c>using</c>.</summary>
internal sealed class Disposer(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}
