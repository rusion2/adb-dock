namespace AdbDock.Core;

// The online device owns one reference. An operation or mirror keeps its own
// reference until it has finished using the token, even across a reconnect.
internal sealed class DeviceConnection
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _references = 1;

    public CancellationToken Token => _lifetime.Token;

    public DeviceConnection Retain()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) _lifetime.Dispose();
    }

    public void Disconnect()
    {
        try { _lifetime.Cancel(); }
        finally { Release(); }
    }
}
