namespace AntiSpam.Bot.Data.Entities;

public class GuildConfig
{
    public ulong GuildId { get; set; }
    
    /// <summary>
    /// Канал для отправки алертов модераторам
    /// </summary>
    public ulong? AlertChannelId { get; set; }
    
    /// <summary>
    /// Включена ли защита от спама
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Минимум каналов для срабатывания (по умолчанию 3)
    /// </summary>
    public int MinChannelsForSpam { get; set; } = 3;
    
    /// <summary>
    /// Окно времени для детекции (в секундах, по умолчанию 120 = 2 минуты)
    /// </summary>
    public int DetectionWindowSeconds { get; set; } = 120;
    
    /// <summary>
    /// Порог похожести сообщений (0.0-1.0, по умолчанию 0.7 = 70%)
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.7;
    
    /// <summary>
    /// Удалять сообщения при обнаружении спама
    /// </summary>
    public bool DeleteMessages { get; set; } = true;
    
    /// <summary>
    /// Мутить пользователя при обнаружении спама
    /// </summary>
    public bool MuteOnSpam { get; set; } = true;
    
    /// <summary>
    /// Длительность мута в минутах (по умолчанию 60)
    /// </summary>
    public int MuteDurationMinutes { get; set; } = 60;
    
    /// <summary>
    /// Detect links from new users (users with low message count)
    /// </summary>
    public bool DetectNewUserLinks { get; set; } = true;
    
    /// <summary>
    /// How long a user is considered "new" (in hours, default 24).
    /// Users who joined within this time are flagged if posting links.
    /// </summary>
    public int NewUserHoursThreshold { get; set; } = 24;
    
    /// <summary>
    /// Allowed link patterns for new user link detection.
    /// Can be full URLs, domains, or partial matches (e.g., "youtube.com", "twitch.tv/mychannel", "github.com/myorg")
    /// </summary>
    public List<string> AllowedLinks { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
