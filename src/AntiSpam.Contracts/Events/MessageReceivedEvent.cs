namespace AntiSpam.Contracts.Events;

public sealed record MessageReceivedEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    
    public required ulong GuildId { get; init; }
    public required ulong ChannelId { get; init; }
    public required ulong MessageId { get; init; }
    public required ulong AuthorId { get; init; }
    
    public required string AuthorUsername { get; init; }
    public required string Content { get; init; }
    public bool IsBot { get; init; }
    
    /// <summary>
    /// Количество вложений (картинки, файлы)
    /// </summary>
    public int AttachmentCount { get; init; }
}
