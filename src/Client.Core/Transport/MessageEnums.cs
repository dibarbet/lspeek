namespace ManualLspClient.Core.Transport;

/// <summary>
/// Direction of a traced LSP message relative to the client.
/// </summary>
public enum MessageDirection
{
    Sent,
    Received
}

/// <summary>
/// Kind of a traced LSP message.
/// </summary>
public enum MessageType
{
    Request,
    Response,
    Notification,
    Stderr
}
