using System.Text.Json;
using Confluent.Kafka;

namespace AntiSpam.Bot.Infrastructure.Kafka;

public class SafeJsonDeserializer<T> : IDeserializer<T> where T : class
{
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull || data.IsEmpty)
        {
            return null!;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(data)!;
        }
        catch
        {
            return null!;
        }
    }
}
