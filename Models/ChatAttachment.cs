namespace OpenclawChat.Models;

public sealed class ChatAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FileName { get; set; } = "image";

    public string MimeType { get; set; } = "image/png";

    public string DataUrl { get; set; } = string.Empty;

    public string Base64Content { get; set; } = string.Empty;
}
