namespace Composa.App.AI;

public sealed record ComfyServerAddress(Uri HttpBase, Uri WebSocket)
{
    public static ComfyServerAddress Parse(string value)
    {
        value = value.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new FormatException("Enter a complete ComfyUI URL beginning with http:// or https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.Trim('/') != "")
            throw new FormatException("The ComfyUI Server URL must not contain credentials, a path, query, or fragment.");
        var builder = new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" };
        var http = builder.Uri;
        builder.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        builder.Path = "/ws";
        return new ComfyServerAddress(http, builder.Uri);
    }

    public Uri Api(string relative) => new(HttpBase, relative.TrimStart('/'));
    public Uri Socket(Guid clientId) => new UriBuilder(WebSocket) { Query = "clientId=" + clientId.ToString("N") }.Uri;
    public override string ToString() => HttpBase.GetLeftPart(UriPartial.Authority);
}
