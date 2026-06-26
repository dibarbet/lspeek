namespace ManualLspClient.Protocol;

/// <summary>
/// Raised when the backend process cannot be launched, fails to signal readiness, or
/// returns an error envelope (HTTP 400 <see cref="ErrorResponse"/>) for an action.
/// </summary>
public sealed class BackendException : Exception
{
    public BackendException(string message) : base(message) { }

    public BackendException(string message, Exception inner) : base(message, inner) { }
}
