using System.Text.Json;
using Confluent.Kafka;

namespace AntiSpam.Gateway;

public sealed class MessagePublisher
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<MessagePublisher> _logger;

    public MessagePublisher(IProducer<string, string> producer, ILogger<MessagePublisher> logger)
    {
        _producer = producer;
        _logger = logger;
    }

    public async Task PublishAsync<T>(string topic, string key, T @event)
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

    public void Flush(TimeSpan timeout)
    {
        var remaining = _producer.Flush(timeout);
        if (remaining > 0)
            _logger.LogWarning("Producer flush left {Count} undelivered messages", remaining);
    }
}
