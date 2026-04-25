namespace OpenclawWebChat.Models;

public sealed class OpenclawConnectionOptions
{
    public string Endpoint { get; set; } = string.Empty;

    public string? Token { get; set; }

    public string? Password { get; set; }

    public string? Origin { get; set; }

    public string SessionKey { get; set; } = "main";
}

