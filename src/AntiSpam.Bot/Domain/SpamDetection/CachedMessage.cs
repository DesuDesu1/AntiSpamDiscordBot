namespace AntiSpam.Bot.Domain.SpamDetection;

public record CachedMessage(
    string Content,
    ulong ChannelId,
    ulong MessageId,
    long Timestamp,
    int AttachmentCount)
{
    public bool HasAttachments => AttachmentCount > 0;
}
