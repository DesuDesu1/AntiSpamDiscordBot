namespace AntiSpam.Bot.Data.Entities;

public class SpamIncident
{
    public long Id { get; set; }
    
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// Контент спам-сообщения
    /// </summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>
    /// Каналы, в которые было отправлено сообщение
    /// </summary>
    public List<ulong> ChannelIds { get; set; } = new();
    
    /// <summary>
    /// Статус: Pending, Banned, Released
    /// </summary>
    public IncidentStatus Status { get; set; } = IncidentStatus.Pending;
    
    /// <summary>
    /// Кто обработал (модератор)
    /// </summary>
    public ulong? HandledByUserId { get; set; }
    public string? HandledByUsername { get; set; }
    
    /// <summary>
    /// Комментарий модератора
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
