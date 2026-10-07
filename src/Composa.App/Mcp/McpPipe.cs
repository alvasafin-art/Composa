namespace Composa.App.Mcp;

/// <summary>
/// Where an MCP client finds the running application. Both ends speak over a named pipe, which .NET backs with a Unix
/// domain socket on Linux and macOS: a name with a directory separator is used as that socket's path, so it lives in
/// Composa's own cache folder rather than in the shared /tmp. On Windows the name is a real named pipe, opened for the
/// current user only. <c>COMPOSA_MCP_PIPE</c> overrides the name, which the tests use to keep their server apart.
/// </summary>
public static class McpPipe
{
    public const string Variable = "COMPOSA_MCP_PIPE";

    public static string Name => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } name ? name : Default;

    public static string Default => OperatingSystem.IsWindows() ? "composa-mcp" : Path.Combine(AppPaths.Cache, "mcp.sock");

    public static string ExchangeName => Environment.GetEnvironmentVariable("COMPOSA_EXCHANGE_PIPE") is { Length: > 0 } name ? name :
        OperatingSystem.IsWindows() ? "composa-image-exchange" : Path.Combine(AppPaths.Cache, "image-exchange.sock");
}
