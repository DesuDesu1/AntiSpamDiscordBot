using AntiSpam.Bot.Services.Cache;

namespace AntiSpam.Bot.Features.SpamDetection;

public record SpamCheckResult(
    bool IsSpam,
    int ChannelCount,
    List<ulong> ChannelIds,
    List<CachedMessage> MatchingMessages,
    SpamReason Reason = SpamReason.None,
    double MaxSimilarity = 0,
    int TotalAttachments = 0);  // Общее количество вложений

public enum SpamReason
{
    None,
    SimilarText,
    AttachmentSpam,
    Both
}

public record SpamDetectionOptions
{
    public int MinChannels { get; init; } = 3;
    public double SimilarityThreshold { get; init; } = 0.7;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(2);
}

public class SpamDetector
{
    private readonly MessageRepository _repository;
    private readonly ILogger<SpamDetector> _logger;

    public SpamDetector(MessageRepository repository, ILogger<SpamDetector> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<SpamCheckResult> CheckAndAddAsync(
        ulong guildId,
        ulong userId,
        CachedMessage message,
        SpamDetectionOptions options)
    {
        var messages = await _repository.GetInWindowAsync(guildId, userId, options.Window);
        await _repository.AddAsync(guildId, userId, message, options.Window);

        // Проверяем по тексту
        var (textMatches, maxSimilarity) = FindSimilarByText(message.Content, messages, options.SimilarityThreshold);
        
        // Проверяем по картинкам — находим все сообщения с вложениями
        var attachmentMatches = message.HasAttachments 
            ? FindMessagesWithAttachments(messages) 
            : new List<CachedMessage>();

        // Собираем все совпадения
        var allTextMatches = textMatches.ToList();
        var allAttachmentMatches = attachmentMatches.ToList();
        
        if (!string.IsNullOrWhiteSpace(message.Content))
            allTextMatches.Add(message);
        if (message.HasAttachments)
            allAttachmentMatches.Add(message);

        // Считаем каналы отдельно для текста и картинок
        var textChannels = allTextMatches.Select(m => m.ChannelId).Distinct().ToList();
        var attachmentChannels = allAttachmentMatches.Select(m => m.ChannelId).Distinct().ToList();

        // Считаем общее количество вложений
        var totalAttachments = allAttachmentMatches.Sum(m => m.AttachmentCount);

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

        // Для удаления берём все сообщения из той категории, которая сработала
        var matchingMessages = reason switch
        {
            SpamReason.Both => allTextMatches.Union(allAttachmentMatches).ToList(),
            SpamReason.SimilarText => allTextMatches,
            SpamReason.AttachmentSpam => allAttachmentMatches,
            _ => new List<CachedMessage>()
        };

        var channelIds = reason switch
        {
            SpamReason.Both => textChannels.Union(attachmentChannels).ToList(),
            SpamReason.SimilarText => textChannels,
            SpamReason.AttachmentSpam => attachmentChannels,
            _ => new List<ulong>()
        };

        if (isSpam)
        {
            _logger.LogWarning(
                "Spam detected: reason={Reason}, textChannels={TextCh}, attachmentChannels={AttCh}, totalAttachments={Att}, similarity={Sim:P0}",
                reason, textChannels.Count, attachmentChannels.Count, totalAttachments, maxSimilarity);
        }

        return new SpamCheckResult(
            IsSpam: isSpam,
            ChannelCount: channelIds.Count,
            ChannelIds: channelIds,
            MatchingMessages: matchingMessages,
            Reason: reason,
            MaxSimilarity: maxSimilarity,
            TotalAttachments: totalAttachments);
    }

    private static (List<CachedMessage> Matches, double MaxSimilarity) FindSimilarByText(
        string content,
        IReadOnlyList<CachedMessage> messages,
        double threshold)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (new List<CachedMessage>(), 0);

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

    private static List<CachedMessage> FindMessagesWithAttachments(IReadOnlyList<CachedMessage> messages)
    {
        return messages.Where(m => m.HasAttachments).ToList();
    }
}
