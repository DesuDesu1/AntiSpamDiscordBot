using System.Text.RegularExpressions;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using AntiSpam.Bot.Infrastructure.Kafka;
using AntiSpam.Bot.Services.Discord;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.Moderation;

public partial class InteractionConsumerWorker : BackgroundService
{
    private readonly ConsumerConfig _config;
    private readonly DiscordService _discord;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly ILogger<InteractionConsumerWorker> _logger;

    public InteractionConsumerWorker(
        ConsumerConfig config,
        DiscordService discord,
        IDbContextFactory<BotDbContext> dbFactory,
        ILogger<InteractionConsumerWorker> logger)
    {
        _config = config;
        _discord = discord;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig(_config)
        {
            GroupId = "antispam-interactions",
            AutoOffsetReset = AutoOffsetReset.Latest
        };

        using var consumer = new ConsumerBuilder<string, InteractionEvent>(config)
            .SetValueDeserializer(new SafeJsonDeserializer<InteractionEvent>())
            .Build();

        consumer.Subscribe(KafkaTopics.Interactions);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Interactions);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result?.Message?.Value == null) continue;

                await ProcessInteractionAsync(result.Message.Value);

                consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error at {Topic}", KafkaTopics.Interactions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing interaction from Kafka");
            }
        }
    }

    private async Task ProcessInteractionAsync(InteractionEvent interaction)
    {
        _logger.LogInformation("Processing {Type} interaction from {User}", 
            interaction.Type, interaction.Username);

        switch (interaction.Type)
        {
            case ModInteractionType.Button:
                await ProcessButtonAsync(interaction);
                break;
            case ModInteractionType.Reaction:
                await ProcessReactionAsync(interaction);
                break;
        }
    }

    private async Task ProcessButtonAsync(InteractionEvent interaction)
    {
        if (string.IsNullOrEmpty(interaction.CustomId)) return;

        // Parse button ID: spam_ban_123 or spam_release_123
        var match = ButtonIdRegex().Match(interaction.CustomId);
        if (!match.Success) return;

        var action = match.Groups[1].Value; // "ban" or "release"
        var incidentId = long.Parse(match.Groups[2].Value);

        var ban = action == "ban";
        await HandleIncidentActionAsync(incidentId, interaction.UserId, interaction.Username, ban);
    }

    private async Task ProcessReactionAsync(InteractionEvent interaction)
    {
        if (interaction.MessageId == null || string.IsNullOrEmpty(interaction.Emoji)) return;

        // Find incident by alert message ID
        await using var db = await _dbFactory.CreateDbContextAsync();
        var incident = await db.SpamIncidents
            .FirstOrDefaultAsync(i => i.AlertMessageId == interaction.MessageId);

        if (incident == null || incident.Status != IncidentStatus.Pending)
        {
            _logger.LogDebug("Incident not found or already handled for message {MessageId}", 
                interaction.MessageId);
            return;
        }

        var ban = interaction.Emoji == "🔨";
        await HandleIncidentActionAsync(incident.Id, interaction.UserId, interaction.Username, ban);
    }

    private async Task HandleIncidentActionAsync(long incidentId, ulong moderatorId, string moderatorName, bool ban)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var incident = await db.SpamIncidents.FindAsync(incidentId);
        if (incident == null || incident.Status != IncidentStatus.Pending)
        {
            _logger.LogWarning("Incident #{Id} not found or already handled", incidentId);
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
        
        // Update alert message
        var action = ban ? "Banned" : "Released";
        await _discord.UpdateAlertMessageAsync(incident.GuildId, incident, action, moderatorName);
        
        _logger.LogInformation("Incident #{Id} {Action} by {Moderator}", 
            incidentId, action, moderatorName);
    }

    [GeneratedRegex(@"spam_(ban|release)_(\d+)")]
    private static partial Regex ButtonIdRegex();
}
