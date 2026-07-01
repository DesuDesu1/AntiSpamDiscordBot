using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.GuildManagement.Links;

public sealed record ListLinksCommand(ulong GuildId) : ICommand<string>;

public sealed class ListLinksHandler(BotDbContext db) : ICommandHandler<ListLinksCommand, string>
{
    public async ValueTask<string> Handle(ListLinksCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.AsNoTracking().GetOrDefaultAsync(command.GuildId, ct);

        if (config.AllowedLinks.Count == 0)
            return "📋 **Allowed Links**\n\nNo links allowed yet.\nUse `/antispam allow-link` to add one.";

        var list = string.Join("\n", config.AllowedLinks.Select(l => $"• `{l}`"));
        return $"📋 **Allowed Links** ({config.AllowedLinks.Count}/100)\n\n{list}";
    }
}

public sealed class ListLinksEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/list-links",
            async (ListLinksCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
