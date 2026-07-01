using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Links;

public sealed record RemoveLinkCommand(ulong GuildId, string Link) : ICommand<string>;

public sealed class RemoveLinkHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<RemoveLinkCommand, string>
{
    public async ValueTask<string> Handle(RemoveLinkCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        var normalized = config.RemoveLink(command.Link);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ Removed `{normalized}` from allowed links";
    }
}

public sealed class RemoveLinkEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/remove-link",
            async (RemoveLinkCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
