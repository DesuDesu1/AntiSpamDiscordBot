using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.GuildManagement.Invites;

public sealed record ListInvitesCommand(ulong GuildId) : ICommand<string>;

public sealed class ListInvitesHandler(BotDbContext db) : ICommandHandler<ListInvitesCommand, string>
{
    public async ValueTask<string> Handle(ListInvitesCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.AsNoTracking().GetOrDefaultAsync(command.GuildId, ct);

        if (config.AllowedInviteServers.Count == 0)
            return "📋 **Allowed Invite Servers**\n\nNone yet - new members can only post invites to this server.\nUse `/antispam allow-invite` to allow another server.";

        var list = string.Join("\n", config.AllowedInviteServers.Select(s => $"• **{s.Name}** (`{s.GuildId}`)"));
        return $"📋 **Allowed Invite Servers** ({config.AllowedInviteServers.Count}/50)\n\n{list}";
    }
}

public sealed class ListInvitesEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/list-invites",
            async (ListInvitesCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
