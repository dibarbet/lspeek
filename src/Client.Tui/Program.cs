using ManualLspClient.Tui.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<ConnectCommand>();
app.Configure(config =>
{
    config.SetApplicationName("lspeek");
    config.SetApplicationVersion("0.1.0");
});

return await app.RunAsync(args);
