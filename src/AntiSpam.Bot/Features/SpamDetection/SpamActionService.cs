using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using AntiSpam.Bot.Features.GuildManagement;
using AntiSpam.Bot.Services.Discord;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.SpamDetection;

public class SpamActionService
{
    private readonly DiscordService _discord;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly GuildConfigService _configService;
    private readonly ILogger<SpamActionService> _logger;

    public SpamActionService(
        DiscordService discord,
        IDbContextFactory<BotDbContext> dbFactory,
        GuildConfigService configService,
        ILogger<SpamActionService> logger)
    {
        _discord = discord;
        _dbFactory = dbFactory;
        _configService = configService;
        _logger = logger;
    }

    public async Task HandleSpamDetectedAsync(
        ulong guildId,
        ulong userId,
        string username,
        string content,
        List<ulong> channelIds,
        List<(ulong ChannelId, ulong MessageId)> messagesToDelete)
    {
        var config = await _configService.GetOrCreateAsync(guildId);
        
        if (!config.IsEnabled)
        {
            _logger.LogDebug("Spam detection disabled for guild {GuildId}", guildId);
            return;
        }

        // 1. Создаём инцидент
        var incident = await CreateIncidentAsync(guildId, userId, username, content, channelIds);
        
        // 2. Удаляем сообщения
        if (config.DeleteMessages)
        {
            await _discord.BulkDeleteMessagesAsync(guildId, messagesToDelete);
        }

        // 3. Мутим пользователя
        if (config.MuteOnSpam)
        {
            await _discord.MuteUserAsync(guildId, userId, TimeSpan.FromMinutes(config.MuteDurationMinutes));
        }

        // 4. Отправляем алерт модераторам
        if (config.AlertChannelId.HasValue)
        {
            await _discord.SendAlertAsync(guildId, config.AlertChannelId.Value, incident, config);
        }
    }

    /// <summary>
    /// Handles suspicious new user posting links (recently joined + link)
    /// </summary>
    public async Task HandleSuspiciousNewUserAsync(
        ulong guildId,
        ulong userId,
        string username,
        string content,
        ulong channelId,
        ulong messageId,
        TimeSpan? memberFor)
    {
        var config = await _configService.GetOrCreateAsync(guildId);
        
        if (!config.IsEnabled || !config.DetectNewUserLinks)
        {
            return;
        }

        var memberForDisplay = memberFor.HasValue 
            ? FormatDuration(memberFor.Value) 
            : "unknown";

        // Create incident with special reason
        var incident = await CreateIncidentAsync(
            guildId, 
            userId, 
            username, 
            $"[NEW USER - joined {memberForDisplay} ago] {content}", 
            new List<ulong> { channelId });

        // Delete the message
        if (config.DeleteMessages)
        {
            await _discord.BulkDeleteMessagesAsync(guildId, new List<(ulong, ulong)> { (channelId, messageId) });
        }

        // Mute the user
        if (config.MuteOnSpam)
        {
            await _discord.MuteUserAsync(guildId, userId, TimeSpan.FromMinutes(config.MuteDurationMinutes));
        }

        // Send alert to moderators
        if (config.AlertChannelId.HasValue)
        {
            await _discord.SendNewUserLinkAlertAsync(guildId, config.AlertChannelId.Value, incident, memberFor, config);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
            return $"{(int)duration.TotalMinutes}m";
        if (duration.TotalHours < 24)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private async Task<SpamIncident> CreateIncidentAsync(
        ulong guildId,
        ulong userId,
        string username,
        string content,
        List<ulong> channelIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var incident = new SpamIncident
        {
            GuildId = guildId,
            UserId = userId,
            Username = username,
            Content = content.Length > 500 ? content[..500] : content,
            ChannelIds = channelIds,
            Status = IncidentStatus.Pending
        };

        db.SpamIncidents.Add(incident);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Created spam incident #{Id} for user {User} in guild {Guild}", 
            incident.Id, username, guildId);
        
        return incident;
    }

    public async Task HandleIncidentActionAsync(long incidentId, ulong moderatorId, string moderatorName, bool ban)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var incident = await db.SpamIncidents.FindAsync(incidentId);
        if (incident == null || incident.Status != IncidentStatus.Pending)
        {
            return;
        }

        incident.Status = ban ? IncidentStatus.Banned : IncidentStatus.Released;
        incident.HandledByUserId = moderatorId;
        incident.HandledByUsername = moderatorName;
        incident.HandledAt = DateTime.UtcNow;

        if (ban)
        {
            await _discord.BanUserAsync(incident.GuildId, incident.UserId, "Spam detected");
        }
        else
        {
            await _discord.UnmuteUserAsync(incident.GuildId, incident.UserId);
        }

        await db.SaveChangesAsync();
        
        _logger.LogInformation("Incident #{Id} {Action} by {Moderator}", 
            incidentId, ban ? "banned" : "released", moderatorName);
    }
}
