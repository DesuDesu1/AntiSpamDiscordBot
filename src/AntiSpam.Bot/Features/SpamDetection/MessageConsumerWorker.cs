using AntiSpam.Bot.Infrastructure.Kafka;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Mediator;

namespace AntiSpam.Bot.Features.SpamDetection;

/// <summary>
/// The only remaining Kafka consumer: message volume is the one stream that's genuinely
/// unbounded and bursty, so it's the one place a broker (backpressure, replay, partitioned
/// consumers) earns its keep. Slash commands and moderation clicks are human-rate and go
/// straight over HTTP instead - see <see cref="AntiSpam.Bot.Features.GuildManagement"/> and
/// <see cref="AntiSpam.Bot.Features.Moderation"/> endpoints.
/// </summary>
public class MessageConsumerWorker : BackgroundService
{
    private readonly ConsumerConfig _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageConsumerWorker> _logger;

    public MessageConsumerWorker(ConsumerConfig config, IServiceScopeFactory scopeFactory, ILogger<MessageConsumerWorker> logger)
    {
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig(_config)
        {
            GroupId = "antispam-messages",
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<string, MessageReceivedEvent>(config)
            .SetValueDeserializer(new SafeJsonDeserializer<MessageReceivedEvent>())
            .Build();

        consumer.Subscribe(KafkaTopics.Messages);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Messages);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result?.Message?.Value == null) continue;

                var message = result.Message.Value;
                if (message.IsBot)
                {
                    _logger.LogDebug("Ignoring bot message from {Author}", message.AuthorUsername);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                await mediator.Send(new DetectSpamCommand(message), stoppingToken);

                consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error at {Topic}", KafkaTopics.Messages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from Kafka");
            }
        }
    }
}
