namespace QtsBilling.Ui.Layout;

/// <summary>
/// The rule that keeps a context switch honest (specification task 13, case T13.02): an operator may move to
/// another business, but not while this screen has a command in flight, and never in a way that makes queued
/// money or an open cash session disappear. The scope of an in-flight command is fixed when it is sent, so this
/// guard is about the operator's attention rather than the server's authority - which refuses the same request
/// twice is a different thing, and the server does that too.
/// </summary>
public sealed class BusinessSwitchGuard
{
    private readonly List<string> _inFlight = [];

    /// <summary>Number of commands this shell has sent and not yet resolved.</summary>
    public int InFlightCount
    {
        get
        {
            lock (_inFlight)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>
    /// Set by a host whose device still holds provisional cash that has not reached the server. A browser has no
    /// such state, so the default is honest: nothing is pending.
    /// </summary>
    public bool HasUnsyncedDeviceCash { get; set; }

    /// <summary>A short label for the reason a switch is refused, or null when it may proceed.</summary>
    public string? BlockedReason
    {
        get
        {
            if (InFlightCount > 0)
            {
                return "A command is still being posted. Wait for its result before changing business.";
            }

            if (HasUnsyncedDeviceCash)
            {
                return "This terminal still holds money that has not reached the server. Settle or sync it before changing business.";
            }

            return null;
        }
    }

    /// <summary>Registers a command for as long as it is in flight; the returned token releases it.</summary>
    public IDisposable Begin(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        lock (_inFlight)
        {
            _inFlight.Add(label);
        }

        return new Registration(this, label);
    }

    /// <summary>
    /// Decides a requested switch. <paramref name="reason"/> carries the refusal so the screen can say why rather
    /// than leaving the operator to guess.
    /// </summary>
    public bool TryRequestSwitch(out string? reason)
    {
        reason = BlockedReason;
        return reason is null;
    }

    private void Release(string label)
    {
        lock (_inFlight)
        {
            _inFlight.Remove(label);
        }
    }

    private sealed class Registration(BusinessSwitchGuard guard, string label) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            guard.Release(label);
        }
    }
}
