namespace AntiSpam.Bot.Services.Cache;

public record CachedMessage(
    string Content,
    ulong ChannelId,
    ulong MessageId,
    long Timestamp,
    int AttachmentCount)
{
    public bool HasAttachments => AttachmentCount > 0;
}
