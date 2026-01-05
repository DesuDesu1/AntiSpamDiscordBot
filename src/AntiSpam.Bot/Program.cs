using System.Text.Json;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<MessageConsumerWorker>();

var host = builder.Build();
host.Run();

public class MessageConsumerWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<MessageConsumerWorker> _logger;

    public MessageConsumerWorker(IConfiguration config, ILogger<MessageConsumerWorker> logger)
    {
        _config = config;
        _logger = logger;
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

                // === Тут твоя логика обработки ===
                await ProcessMessageAsync(message);
                
                // Коммитим после успешной обработки
                consumer.Commit(result);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
            }
        }

        consumer.Close();
    }

    private Task ProcessMessageAsync(MessageReceivedEvent message)
    {
        // Простой пример: логируем сообщение
        _logger.LogInformation(
            "[{Guild}] {Author}: {Content}",
            message.GuildId,
            message.AuthorUsername,
            message.Content.Length > 50 ? message.Content[..50] + "..." : message.Content);

        // TODO: Здесь будет:
        // - Проверка на спам
        // - Расчёт trust score
        // - Сохранение в Redis buffer
        // - Бан если спам

        return Task.CompletedTask;
    }
}
