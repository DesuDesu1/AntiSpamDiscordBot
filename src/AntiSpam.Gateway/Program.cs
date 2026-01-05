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
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
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

        var json = JsonSerializer.Serialize(@event);
        var key = message.Author.Id.ToString();

        try
        {
            await _producer.ProduceAsync(
                KafkaTopics.Messages,
                new Message<string, string> { Key = key, Value = json });

            _logger.LogDebug("Published message {MessageId} from {Author}", 
                message.Id, message.Author.Username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message {MessageId}", message.Id);
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
