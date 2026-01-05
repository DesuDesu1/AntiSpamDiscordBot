using System.Text.Json;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;

namespace AntiSpam.Bot.Features.SpamDetection;

public class MessageConsumerWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<MessageConsumerWorker> _logger;
    private readonly UserMessageCache _cache;

    public MessageConsumerWorker(
        IConfiguration config, 
        ILogger<MessageConsumerWorker> logger,
        UserMessageCache cache)
    {
        _config = config;
        _logger = logger;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _config["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = _config["Kafka:GroupId"] ?? "antispam-bot",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(KafkaTopics.Messages);

        _logger.LogInformation("Consumer started, listening to {Topic}", KafkaTopics.Messages);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                
                if (result?.Message?.Value == null)
                    continue;

                var message = JsonSerializer.Deserialize<MessageReceivedEvent>(result.Message.Value);
                
                if (message == null)
                    continue;

                await ProcessMessageAsync(message);
                
                consumer.Commit(result);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
            }
        }

        consumer.Close();
    }

    private async Task ProcessMessageAsync(MessageReceivedEvent message)
    {
        // 1. Кэшируем сообщение
        var cached = new CachedMessage(
            Content: message.Content,
            ChannelId: message.ChannelId,
            MessageId: message.MessageId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );

        var messageCount = await _cache.AddMessageAsync(
            message.GuildId, 
            message.AuthorId, 
            cached);

        _logger.LogInformation(
            "[{Guild}] {Author}: {Content} (msgs in hour: {Count})",
            message.GuildId,
            message.AuthorUsername,
            message.Content.Length > 50 ? message.Content[..50] + "..." : message.Content,
            messageCount);

        // 2. Проверяем на спам
        var isSpam = await DetectSpamAsync(message, messageCount);
        
        if (isSpam)
        {
            _logger.LogWarning("SPAM detected from {User} in guild {Guild}", 
                message.AuthorUsername, message.GuildId);
            
            // TODO: Отправить команду на бан/мут через Kafka
        }
    }

    private async Task<bool> DetectSpamAsync(MessageReceivedEvent message, int recentMessageCount)
    {
        // TODO: Реальная логика детекции
        // - Rate limit (слишком много сообщений)
        // - Duplicate content
        // - Spam patterns (ссылки, @everyone, etc)
        // - Trust score check
        
        // Пока простая проверка: больше 10 сообщений в час = подозрительно
        if (recentMessageCount > 10)
        {
            var messages = await _cache.GetRecentMessagesAsync(message.GuildId, message.AuthorId);
            
            // Проверка на дубликаты
            var duplicates = messages
                .GroupBy(m => m.Content)
                .Where(g => g.Count() > 3)
                .Any();

            if (duplicates)
            {
                return true;
            }
        }

        return false;
    }
}
