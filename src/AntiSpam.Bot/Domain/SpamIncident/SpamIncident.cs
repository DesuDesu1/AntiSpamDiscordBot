namespace AntiSpam.Bot.Domain.SpamIncident;

public enum IncidentStatus
{
    Pending = 0,
    Banned = 1,
    Released = 2
}

public enum IncidentOutcome
{
    Ban,
    Release
}

/// <summary>
/// A flagged spam event awaiting (or having received) moderator action. Aggregate root:
/// the Pending -> Banned/Released transition can only happen through <see cref="Resolve"/>,
/// which makes double-handling the same incident (two moderators, a duplicate button click,
/// Kafka redelivery) structurally impossible instead of relying on ad-hoc status checks
/// scattered across every caller.
/// </summary>
public sealed class SpamIncident
{
    private const int MaxContentLength = 500;

    public long Id { get; private set; }
    public ulong GuildId { get; private set; }
    public ulong UserId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;

    private readonly List<ulong> _channelIds = new();
    public IReadOnlyList<ulong> ChannelIds => _channelIds;

    public IncidentStatus Status { get; private set; } = IncidentStatus.Pending;
    public ulong? AlertMessageId { get; private set; }
    public ulong? AlertChannelId { get; private set; }
    public ulong? HandledByUserId { get; private set; }
    public string? HandledByUsername { get; private set; }
    public string? ModeratorNote { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? HandledAt { get; private set; }

    private SpamIncident()
    {
        // EF Core materialization.
    }

    public static SpamIncident Raise(
        ulong guildId, ulong userId, string username, string content, IEnumerable<ulong> channelIds)
    {
        var incident = new SpamIncident
        {
            GuildId = guildId,
            UserId = userId,
            Username = username,
            Content = content.Length > MaxContentLength ? content[..MaxContentLength] : content,
        };
        incident._channelIds.AddRange(channelIds);
        return incident;
    }

    /// <summary>Records where the moderator alert embed landed, so reaction-based resolution can find it.</summary>
    public void AttachAlert(ulong channelId, ulong messageId)
    {
        AlertChannelId = channelId;
        AlertMessageId = messageId;
    }

    /// <exception cref="IncidentAlreadyHandledException">The incident isn't Pending anymore.</exception>
    public void Resolve(ulong moderatorId, string moderatorName, IncidentOutcome outcome)
    {
        if (Status != IncidentStatus.Pending)
            throw new IncidentAlreadyHandledException($"Incident #{Id} is already {Status}");

        Status = outcome == IncidentOutcome.Ban ? IncidentStatus.Banned : IncidentStatus.Released;
        HandledByUserId = moderatorId;
        HandledByUsername = moderatorName;
        HandledAt = DateTime.UtcNow;
    }
}
