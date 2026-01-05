namespace AntiSpam.Contracts.Events;

public sealed record InteractionEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    
    public required ulong GuildId { get; init; }
    public required ulong ChannelId { get; init; }
    public required ulong UserId { get; init; }
    public required string Username { get; init; }
    public required ModInteractionType Type { get; init; }
    
    // For button interactions
    public string? CustomId { get; init; }
    
    // For reaction interactions
    public ulong? MessageId { get; init; }
    public string? Emoji { get; init; }
}

public enum ModInteractionType
{
    Button,
    Reaction
}
