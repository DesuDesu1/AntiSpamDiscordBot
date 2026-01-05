namespace AntiSpam.Contracts.Events;

public sealed record SlashCommandEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    
    public required ulong GuildId { get; init; }
    public required ulong ChannelId { get; init; }
    public required ulong UserId { get; init; }
    public required string Username { get; init; }
    
    public required string CommandName { get; init; }
    public required string SubCommandName { get; init; }
    public Dictionary<string, object> Options { get; init; } = new();
    
    // For responding via webhook
    public required ulong InteractionId { get; init; }
    public required string InteractionToken { get; init; }
}
