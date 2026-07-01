using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Domain.GuildManagement;
using AntiSpam.Bot.Domain.SpamDetection;
using AntiSpam.Bot.Infrastructure.Cache;
using AntiSpam.Bot.Infrastructure.Discord;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Invites;

/// <param name="Invite">An invite link/code to the server, or its raw guild id (so it can be removed even after the invite expires).</param>
public sealed record RemoveInviteCommand(ulong GuildId, string Invite) : ICommand<string>;

public sealed class RemoveInviteHandler(BotDbContext db, GuildConfigCache cache, DiscordService discord)
    : ICommandHandler<RemoveInviteCommand, string>
{
    public async ValueTask<string> Handle(RemoveInviteCommand command, CancellationToken ct)
    {
        var guildId = await ResolveTargetGuildIdAsync(command.Invite);

        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);
        var name = config.RemoveInviteServer(guildId);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ Removed **{name}** from the invite allow-list";
    }

    // Accept either a raw guild id (works even if the invite has since expired) or an invite link.
    private async Task<ulong> ResolveTargetGuildIdAsync(string input)
    {
        var trimmed = input.Trim();
        if (ulong.TryParse(trimmed, out var guildId))
            return guildId;

        var resolved = await discord.ResolveInviteAsync(LinkPolicy.ParseInviteCode(trimmed))
            ?? throw new InvalidConfigValueException("Couldn't resolve that invite - pass a valid link or the server id from /antispam list-invites");
        return resolved.GuildId;
    }
}

public sealed class RemoveInviteEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/remove-invite",
            async (RemoveInviteCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
