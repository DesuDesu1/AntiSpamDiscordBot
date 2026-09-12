namespace AntiSpam.Bot.Domain.SpamDetection;

public record SpamDetectionOptions
{
    public int MinChannels { get; init; } = 3;
    public double SimilarityThreshold { get; init; } = 0.7;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(2);
}

public enum SpamReason
{
    None,
    SimilarText,
    AttachmentSpam,
    Both
}

public record SpamVerdict(
    bool IsSpam,
    int ChannelCount,
    IReadOnlyList<ulong> ChannelIds,
    IReadOnlyList<CachedMessage> MatchingMessages,
    SpamReason Reason = SpamReason.None,
    double MaxSimilarity = 0,
    int TotalAttachments = 0);

public sealed class MessageWindow(IReadOnlyList<CachedMessage> recentMessages)
{
    public SpamVerdict Evaluate(CachedMessage newMessage, SpamDetectionOptions options)
    {
        var (textMatches, maxSimilarity) = FindSimilarByText(newMessage.Content, recentMessages, options.SimilarityThreshold);
        var attachmentMatches = newMessage.HasAttachments
            ? recentMessages.Where(m => m.HasAttachments).ToList()
            : [];

        if (!string.IsNullOrWhiteSpace(newMessage.Content))
            textMatches.Add(newMessage);
        if (newMessage.HasAttachments)
            attachmentMatches.Add(newMessage);

        var textChannels = textMatches.Select(m => m.ChannelId).Distinct().ToList();
        var attachmentChannels = attachmentMatches.Select(m => m.ChannelId).Distinct().ToList();
        var totalAttachments = attachmentMatches.Sum(m => m.AttachmentCount);

        var isTextSpam = textChannels.Count >= options.MinChannels;
        var isAttachmentSpam = attachmentChannels.Count >= options.MinChannels;
        var isSpam = isTextSpam || isAttachmentSpam;

        var reason = (isTextSpam, isAttachmentSpam) switch
        {
            (true, true) => SpamReason.Both,
            (true, false) => SpamReason.SimilarText,
            (false, true) => SpamReason.AttachmentSpam,
            _ => SpamReason.None
        };

        var matchingMessages = reason switch
        {
            SpamReason.Both => textMatches.Union(attachmentMatches).ToList(),
            SpamReason.SimilarText => textMatches,
            SpamReason.AttachmentSpam => attachmentMatches,
            _ => []
        };

        var channelIds = reason switch
        {
            SpamReason.Both => textChannels.Union(attachmentChannels).ToList(),
            SpamReason.SimilarText => textChannels,
            SpamReason.AttachmentSpam => attachmentChannels,
            _ => []
        };

        return new SpamVerdict(isSpam, channelIds.Count, channelIds, matchingMessages, reason, maxSimilarity, totalAttachments);
    }

    private static (List<CachedMessage> Matches, double MaxSimilarity) FindSimilarByText(
        string content, IReadOnlyList<CachedMessage> messages, double threshold)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ([], 0);

        var matches = new List<CachedMessage>();
        var maxSimilarity = 0.0;

        foreach (var msg in messages)
        {
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;

            var similarity = TextSimilarity.Calculate(content, msg.Content);

            if (similarity > maxSimilarity)
                maxSimilarity = similarity;

            if (similarity >= threshold)
                matches.Add(msg);
        }

        return (matches, maxSimilarity);
    }
}
