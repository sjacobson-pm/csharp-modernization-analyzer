using Analyzer.Mcp;

var transport = GetOption(args, "--transport") ?? "stdio";
var portValue = GetOption(args, "--port");
var port = int.TryParse(portValue, out var parsedPort) ? parsedPort : 3000;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

var server = new McpServer();
await server.RunAsync(transport, port, cancellationSource.Token);

static string? GetOption(string[] args, string optionName)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}
