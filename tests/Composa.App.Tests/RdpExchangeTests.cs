using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Composa.App.Mcp;

namespace Composa.App.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class RdpExchangeTests
{
    [Fact]
    public async Task Sid_names_are_distinct_and_parallel_local_channels_have_owner_only_acl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
        var prefix = "composa-test-" + Guid.NewGuid().ToString("N");
        var a = McpPipe.WindowsName(prefix, sid); var b = McpPipe.WindowsName(prefix, "S-1-5-21-1-2-3-1002");
        Assert.NotEqual(a, b); Assert.Equal("composa-image-exchange-" + sid, McpPipe.WindowsName("composa-image-exchange", sid));
        using var first = McpPipe.CreateServer(a); using var second = McpPipe.CreateServer(b);
        var progress = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        async Task Exchange(NamedPipeServerStream server, string name, byte value)
        {
            progress[name] = "waiting for client";
            var accepted = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, McpPipe.ClientOptions);
            await client.ConnectAsync(3000, TestContext.Current.CancellationToken); await accepted; McpPipe.VerifyOwner(client);
            progress[name] = "connected";
            var acl = server.GetAccessControl(); Assert.Equal(sid, acl.GetOwner(typeof(SecurityIdentifier))!.Value);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.All(rules.Where(rule => rule.AccessControlType == AccessControlType.Allow), rule => Assert.Equal(sid, rule.IdentityReference.Value));
            Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-2" && rule.AccessControlType == AccessControlType.Deny);
            progress[name] = "ACL verified";
            var received = new byte[1]; var read = server.ReadExactlyAsync(received, TestContext.Current.CancellationToken).AsTask();
            await client.WriteAsync(new byte[] { value }, TestContext.Current.CancellationToken);
            await read; Assert.Equal(value, received[0]);
            progress[name] = "server received";
            read = client.ReadExactlyAsync(received, TestContext.Current.CancellationToken).AsTask();
            await server.WriteAsync(new byte[] { (byte)(value + 1) }, TestContext.Current.CancellationToken);
            await read; Assert.Equal(value + 1, received[0]);
            progress[name] = "done";
        }
        // Different names exercise the global pipe namespace; a cross-account
        // RDP test additionally needs a Windows Server with separate identities.
        try
        {
            await Task.WhenAll(Task.Run(() => Exchange(first, a, 10), TestContext.Current.CancellationToken),
                Task.Run(() => Exchange(second, b, 20), TestContext.Current.CancellationToken)).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException) { throw new TimeoutException(string.Join("; ", progress.Select(pair => pair.Key + ": " + pair.Value))); }
    }
}
