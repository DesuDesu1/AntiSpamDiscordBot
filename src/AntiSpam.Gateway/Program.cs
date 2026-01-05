using System.Text.Json;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Discord;
using Discord.WebSocket;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<DiscordGatewayWorker>();
builder.Services.AddSingleton<DiscordSocketClient>(_ =>
{
    var config = new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds 
            | GatewayIntents.GuildMessages 
            | GatewayIntents.MessageContent
            | GatewayIntents.GuildMessageReactions
    };
    return new DiscordSocketClient(config);
});
builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var config = new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
        Acks = Acks.All,
        EnableIdempotence = true
    };
    return new ProducerBuilder<string, string>(config).Build();
});

var host = builder.Build();
host.Run();

public class DiscordGatewayWorker : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly IProducer<string, string> _producer;
    private readonly IConfiguration _config;
    private readonly ILogger<DiscordGatewayWorker> _logger;

    public DiscordGatewayWorker(
        DiscordSocketClient client,
        IProducer<string, string> producer,
        IConfiguration config,
        ILogger<DiscordGatewayWorker> logger)
    {
        _client = client;
        _producer = producer;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.Log += LogAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        _client.ReactionAdded += OnReactionAddedAsync;

        var token = _config["Discord:Token"] 
            ?? throw new InvalidOperationException("Discord:Token not configured");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _logger.LogInformation("Discord Gateway started");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage)
            return;

        if (message.Channel is not SocketGuildChannel guildChannel)
            return;

        var @event = new MessageReceivedEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = guildChannel.Guild.Id,
            ChannelId = message.Channel.Id,
            MessageId = message.Id,
            AuthorId = message.Author.Id,
            AuthorUsername = message.Author.Username,
            Content = message.Content,
            IsBot = message.Author.IsBot,
            AttachmentCount = message.Attachments.Count
        };

        await PublishAsync(KafkaTopics.Messages, message.Author.Id.ToString(), @event);
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.GuildId == null) return;
        
        // Immediately acknowledge the button
        await component.DeferAsync();
        
        var @event = new InteractionEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = component.GuildId.Value,
            ChannelId = component.Channel.Id,
            UserId = component.User.Id,
            Username = component.User.Username,
            Type = ModInteractionType.Button,
            CustomId = component.Data.CustomId,
            MessageId = component.Message.Id
        };

        await PublishAsync(KafkaTopics.Interactions, component.User.Id.ToString(), @event);
        
        _logger.LogInformation("Button {CustomId} clicked by {User}", 
            component.Data.CustomId, component.User.Username);
    }

    private async Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> message, 
        Cacheable<IMessageChannel, ulong> channel, 
        SocketReaction reaction)
    {
        // Ignore bot reactions
        if (reaction.UserId == _client.CurrentUser?.Id) return;
        
        // Only process in guilds
        if (channel.Value is not SocketGuildChannel guildChannel) return;

        // Only process specific emoji: 🔨 (ban) or ✅ (release)
        var emote = reaction.Emote.Name;
        if (emote != "🔨" && emote != "✅") return;

        var @event = new InteractionEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = guildChannel.Guild.Id,
            ChannelId = channel.Id,
            UserId = reaction.UserId,
            Username = reaction.User.IsSpecified ? reaction.User.Value.Username : "Unknown",
            Type = ModInteractionType.Reaction,
            MessageId = message.Id,
            Emoji = emote
        };

        await PublishAsync(KafkaTopics.Interactions, reaction.UserId.ToString(), @event);
        
        _logger.LogInformation("Reaction {Emoji} added by user {UserId} on message {MessageId}", 
            emote, reaction.UserId, message.Id);
    }

    private async Task PublishAsync<T>(string topic, string key, T @event)
    {
        try
        {
            var json = JsonSerializer.Serialize(@event);
            await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = json });
            _logger.LogDebug("Published to {Topic}: {Key}", topic, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish to {Topic}", topic);
        }
    }

    private Task LogAsync(Discord.LogMessage log)
    {
        _logger.Log(
            log.Severity switch
            {
                LogSeverity.Critical => LogLevel.Critical,
                LogSeverity.Error => LogLevel.Error,
                LogSeverity.Warning => LogLevel.Warning,
                LogSeverity.Info => LogLevel.Information,
                _ => LogLevel.Debug
            },
            log.Exception,
            "{Source}: {Message}",
            log.Source,
            log.Message);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
