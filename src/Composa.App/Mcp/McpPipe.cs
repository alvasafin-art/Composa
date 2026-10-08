using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

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

    public static string Default => OperatingSystem.IsWindows() ? WindowsName("composa-mcp", UserSid()) : Path.Combine(AppPaths.Cache, "mcp.sock");

    public static string ExchangeName => Environment.GetEnvironmentVariable("COMPOSA_EXCHANGE_PIPE") is { Length: > 0 } name ? name :
        OperatingSystem.IsWindows() ? WindowsName("composa-image-exchange", UserSid()) : Path.Combine(AppPaths.Cache, "image-exchange.sock");

    internal static string WindowsName(string prefix, string sid) => prefix + "-" + sid;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new UnauthorizedAccessException("The Windows user SID is unavailable.");
    }

    internal static PipeOptions ClientOptions => PipeOptions.Asynchronous | (OperatingSystem.IsWindows() ? PipeOptions.None : PipeOptions.CurrentUserOnly);

    internal static void VerifyOwner(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return;
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner?.Value != UserSid()) throw new UnauthorizedAccessException("The Composa pipe belongs to another Windows user.");
    }

    internal static NamedPipeServerStream CreateServer(string name)
    {
        if (!OperatingSystem.IsWindows()) return new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.ReadWrite, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}
