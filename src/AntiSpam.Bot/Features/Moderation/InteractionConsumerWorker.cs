using System.Text.Json;
using System.Text.RegularExpressions;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using AntiSpam.Bot.Services.Discord;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.Moderation;

public partial class InteractionConsumerWorker : BackgroundService
{
    private readonly IConsumer<string, string> _consumer;
    private readonly DiscordService _discord;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly ILogger<InteractionConsumerWorker> _logger;

    public InteractionConsumerWorker(
        IServiceProvider services,
        IConfiguration config,
        ILogger<InteractionConsumerWorker> logger)
    {
        _discord = services.GetRequiredService<DiscordService>();
        _dbFactory = services.GetRequiredService<IDbContextFactory<BotDbContext>>();
        _logger = logger;

        var kafkaConfig = new ConsumerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = "antispam-interactions",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };
        _consumer = new ConsumerBuilder<string, string>(kafkaConfig).Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(KafkaTopics.Interactions);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Interactions);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(TimeSpan.FromSeconds(1));
                if (result == null) continue;

                var interaction = JsonSerializer.Deserialize<InteractionEvent>(result.Message.Value);
                if (interaction == null) continue;

                await ProcessInteractionAsync(interaction);

                _consumer.Commit(result);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing interaction");
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
        await _discord.UpdateAlertMessageAsync(incident, action, moderatorName);
        
        _logger.LogInformation("Incident #{Id} {Action} by {Moderator}", 
            incidentId, action, moderatorName);
    }

    [GeneratedRegex(@"spam_(ban|release)_(\d+)")]
    private static partial Regex ButtonIdRegex();

    public override void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
        base.Dispose();
    }
}
