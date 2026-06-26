using Lspeek.Tui.Session;

namespace Lspeek.Tui.Presentation;

/// <summary>
/// Maps <see cref="MessageStatus"/> values to their display label and Spectre.Console color.
/// Keeps presentation concerns out of the session model.
/// </summary>
public static class MessageStatusStyles
{
    /// <summary>Short status label for collapsed log display.</summary>
    public static string Label(MessageStatus status) => status switch
    {
        MessageStatus.Sent => "Sent",
        MessageStatus.Pending => "Pending",
        MessageStatus.Ok => "OK",
        MessageStatus.Error => "Error",
        MessageStatus.Info => "INFO",
        MessageStatus.Warn => "WARN",
        MessageStatus.Stderr => "stderr",
        _ => "?"
    };

    /// <summary>Spectre.Console color for the given status.</summary>
    public static string Color(MessageStatus status) => status switch
    {
        MessageStatus.Ok => "green",
        MessageStatus.Error => "red",
        MessageStatus.Pending => "yellow",
        MessageStatus.Warn => "yellow",
        MessageStatus.Info => "blue",
        MessageStatus.Stderr => "red",
        _ => "dim"
    };
}
