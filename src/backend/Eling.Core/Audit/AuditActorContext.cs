namespace Eling.Core.Audit;

public static class AuditActorContext
{
    private static readonly AsyncLocal<AuditActor?> _current = new();

    public static AuditActor Current => _current.Value ?? AuditActor.Unknown;

    public static IDisposable Begin(AuditActor actor)
    {
        var previous = _current.Value;
        _current.Value = actor;
        return new Scope(previous);
    }

    private sealed class Scope(AuditActor? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
