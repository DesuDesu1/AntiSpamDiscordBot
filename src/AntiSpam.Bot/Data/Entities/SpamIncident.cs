namespace AntiSpam.Bot.Data.Entities;

public class SpamIncident
{
    public long Id { get; set; }
    
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// Content of the spam message
    /// </summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>
    /// Channels where the spam was posted
    /// </summary>
    public List<ulong> ChannelIds { get; set; } = new();
    
    /// <summary>
    /// Status: Pending, Banned, Released
    /// </summary>
    public IncidentStatus Status { get; set; } = IncidentStatus.Pending;
    
    /// <summary>
    /// Alert message ID in the mod channel (for reaction handling)
    /// </summary>
    public ulong? AlertMessageId { get; set; }
    
    /// <summary>
    /// Alert channel ID
    /// </summary>
    public ulong? AlertChannelId { get; set; }
    
    /// <summary>
    /// Who handled (moderator)
    /// </summary>
    public ulong? HandledByUserId { get; set; }
    public string? HandledByUsername { get; set; }
    
    /// <summary>
    /// Moderator note
    /// </summary>
    public string? ModeratorNote { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? HandledAt { get; set; }
}

public enum IncidentStatus
{
    Pending = 0,
    Banned = 1,
    Released = 2
}
