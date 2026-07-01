using System.Text;
using Discord.WebSocket;

namespace AntiSpam.Gateway;

/// <summary>Discord caps message content at 2000 chars. These helpers let a slash-command reply of any
/// length be sent as one or more followups instead of throwing when the reply is long.</summary>
public static class DiscordFollowupExtensions
{
    private const int DiscordMessageLimit = 2000;

    /// <summary>Sends <paramref name="content"/> as one or more ephemeral-capable followups, splitting on
    /// line boundaries so it never trips Discord's 2000-char message limit.</summary>
    public static async Task FollowupChunkedAsync(this SocketSlashCommand command, string content, bool ephemeral = false)
    {
        foreach (var chunk in Chunk(content))
            await command.FollowupAsync(chunk, ephemeral: ephemeral);
    }

    /// <summary>Splits content into ≤2000-char chunks, preferring line boundaries; a single over-long
    /// line is hard-split.</summary>
    private static IEnumerable<string> Chunk(string content)
    {
        if (content.Length <= DiscordMessageLimit)
        {
            yield return content;
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            if (line.Length > DiscordMessageLimit)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                for (var i = 0; i < line.Length; i += DiscordMessageLimit)
                    yield return line.Substring(i, Math.Min(DiscordMessageLimit, line.Length - i));
                continue;
            }

            if (sb.Length + line.Length + 1 > DiscordMessageLimit)
            {
                yield return sb.ToString();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }
}
