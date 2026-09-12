using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Domain.GuildManagement;
using AntiSpam.Bot.Domain.SpamDetection;
using AntiSpam.Bot.Infrastructure.Cache;
using AntiSpam.Bot.Infrastructure.Discord;
using Mediator;
using ZiggyCreatures.Caching.Fusion;

namespace AntiSpam.Bot.Features.GuildManagement.Invites;

public sealed record AllowInviteCommand(ulong GuildId, string Invite) : ICommand<string>;

public sealed class AllowInviteHandler(BotDbContext db, IFusionCache cache, DiscordService discord)
    : ICommandHandler<AllowInviteCommand, string>
{
    public async ValueTask<string> Handle(AllowInviteCommand command, CancellationToken ct)
    {
        var code = LinkPolicy.ParseInviteCode(command.Invite);
        var resolved = await discord.ResolveInviteAsync(code)
            ?? throw new InvalidConfigValueException("Couldn't resolve that invite - is the link valid and not expired?");

        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);
        var name = config.AllowInviteServer(resolved.GuildId, resolved.GuildName);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.GuildConfig(command.GuildId));
        return $"✅ New members may now post invites to **{name}**";
    }
}

public sealed class AllowInviteEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/allow-invite",
            async (AllowInviteCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
