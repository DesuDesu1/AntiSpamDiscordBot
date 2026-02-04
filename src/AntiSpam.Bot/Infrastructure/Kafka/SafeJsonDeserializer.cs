using System.Text.Json;
using Confluent.Kafka;

namespace AntiSpam.Bot.Infrastructure.Kafka;

/// <summary>
/// Безопасный десериализатор для Kafka, который не роняет воркер при получении некорректных данных (Poison Pill).
/// Работает напрямую с ReadOnlySpan<byte> для минимизации аллокаций.
/// </summary>
/// <typeparam name="T">Тип сообщения для десериализации.</typeparam>
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
            // Используем высокопроизводительный Span-based десериализатор
            return JsonSerializer.Deserialize<T>(data)!;
        }
        catch
        {
            // В случае ошибки десериализации (битый JSON и т.д.) возвращаем null.
            // Это позволяет воркеру пропустить Poison Pill сообщение вместо падения.
            return null!;
        }
    }
}
