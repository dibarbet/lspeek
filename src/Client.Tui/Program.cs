using System.Text;
using ManualLspClient.Tui.Commands;
using Spectre.Console.Cli;

if (!Console.IsInputRedirected)
{
    Console.InputEncoding = Encoding.UTF8;
}

if (!Console.IsOutputRedirected)
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}

var app = new CommandApp<ConnectCommand>();
app.Configure(config =>
{
    config.SetApplicationName("lspeek");
    config.SetApplicationVersion("0.1.0");
});

return await app.RunAsync(args);
