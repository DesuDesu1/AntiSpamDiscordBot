using AntiSpam.Bot.Infrastructure.Kafka;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Mediator;

namespace AntiSpam.Bot.Features.SpamDetection;

public class MessageConsumerWorker : BackgroundService
{
    private const int CommitEveryMessages = 100;
    private static readonly TimeSpan CommitEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConsumePoll = TimeSpan.FromMilliseconds(500);

    private readonly ConsumerConfig _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageConsumerWorker> _logger;

    public MessageConsumerWorker(IConfiguration configuration, IServiceScopeFactory scopeFactory, ILogger<MessageConsumerWorker> logger)
    {
        _config = new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is not configured"),
            GroupId = "antispam-messages",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, MessageReceivedEvent>(_config)
            .SetValueDeserializer(new SafeJsonDeserializer<MessageReceivedEvent>())
            .Build();

        consumer.Subscribe(KafkaTopics.Messages);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Messages);

        await Task.Yield();

        var pending = 0;
        var lastCommit = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ConsumePoll);
                if (result == null)
                {
                    if (pending > 0 && DateTime.UtcNow - lastCommit >= CommitEvery)
                        Flush(consumer, ref pending, ref lastCommit);
                    continue;
                }

                if (result.Message?.Value == null)
                {
                    consumer.StoreOffset(result);
                    pending++;
                }
                else
                {
                    var message = result.Message.Value;
                    if (message.IsBot)
                    {
                        _logger.LogDebug("Ignoring bot message from {Author}", message.AuthorUsername);
                    }
                    else
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                        await mediator.Send(new DetectSpamCommand(message), stoppingToken);
                    }

                    consumer.StoreOffset(result);
                    pending++;
                }

                if (pending >= CommitEveryMessages || DateTime.UtcNow - lastCommit >= CommitEvery)
                    Flush(consumer, ref pending, ref lastCommit);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error at {Topic}", KafkaTopics.Messages);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from Kafka");
            }
        }

        if (pending > 0)
        {
            try
            {
                Flush(consumer, ref pending, ref lastCommit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush Kafka offsets on shutdown");
            }
        }
    }

    private void Flush(IConsumer<string, MessageReceivedEvent> consumer, ref int pending, ref DateTime lastCommit)
    {
        consumer.Commit();
        pending = 0;
        lastCommit = DateTime.UtcNow;
    }
}
