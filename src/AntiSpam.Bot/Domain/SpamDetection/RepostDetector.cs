using AntiSpam.Bot.Domain.GuildManagement;

namespace AntiSpam.Bot.Domain.SpamDetection;

public enum SpamReason
{
    SimilarText,
    AttachmentSpam,
    Both
}

public sealed record SpamVerdict(
    IReadOnlyList<ulong> ChannelIds,
    IReadOnlyList<CachedMessage> MatchingMessages,
    SpamReason Reason,
    double MaxSimilarity);

public sealed class RepostDetector(IReadOnlyList<CachedMessage> recentMessages)
{
    public SpamVerdict? Evaluate(CachedMessage newMessage, GuildConfig config)
    {
        var (textMatches, maxSimilarity) =
            FindSimilarByText(newMessage.Content, recentMessages, config.SimilarityThreshold);

        var attachmentMatches = newMessage.HasAttachments
            ? recentMessages.Where(m => m.HasAttachments).ToList()
            : [];

        if (!string.IsNullOrWhiteSpace(newMessage.Content))
            textMatches.Add(newMessage);
        if (newMessage.HasAttachments)
            attachmentMatches.Add(newMessage);

        var textChannels = textMatches.Select(m => m.ChannelId).Distinct().ToList();
        var attachmentChannels = attachmentMatches.Select(m => m.ChannelId).Distinct().ToList();

        var isTextSpam = textChannels.Count >= config.MinChannelsForSpam;
        var isAttachmentSpam = attachmentChannels.Count >= config.MinChannelsForSpam;

        return (isTextSpam, isAttachmentSpam) switch
        {
            (true, true) => new SpamVerdict(
                textChannels.Union(attachmentChannels).ToList(),
                textMatches.Union(attachmentMatches).ToList(),
                SpamReason.Both,
                maxSimilarity),
            (true, false) => new SpamVerdict(textChannels, textMatches, SpamReason.SimilarText, maxSimilarity),
            (false, true) => new SpamVerdict(attachmentChannels, attachmentMatches, SpamReason.AttachmentSpam, maxSimilarity),
            _ => null
        };
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
