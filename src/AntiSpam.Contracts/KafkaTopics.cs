namespace AntiSpam.Contracts;

/// <summary>
/// Messages is the only event stream still on Kafka: it's the one genuinely unbounded, bursty
/// flow. Slash commands and moderation clicks (bans/releases via button or reaction) are
/// human-rate and go straight from Gateway to Bot over HTTP - see the internal endpoints under
/// AntiSpam.Bot.Features.GuildManagement/Moderation.
/// </summary>
public static class KafkaTopics
{
    public const string Messages = "antispam.messages";
}
