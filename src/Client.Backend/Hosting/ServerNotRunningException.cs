namespace Lspeek.Backend.Hosting;

/// <summary>
/// Thrown when an action that requires a live LSP server is attempted while none is running.
/// Modeled as an <see cref="InvalidOperationException"/> (the operation is invalid for the current
/// state) but given a dedicated type so the HTTP layer can map it to <c>409 Conflict</c> by type
/// rather than by matching the message text.
/// </summary>
public sealed class ServerNotRunningException : InvalidOperationException
{
    public ServerNotRunningException()
        : base("Server is not running. Call start_server first.")
    {
    }

    public ServerNotRunningException(string message)
        : base(message)
    {
    }
}
