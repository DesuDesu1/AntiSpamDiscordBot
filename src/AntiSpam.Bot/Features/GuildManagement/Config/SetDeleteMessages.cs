using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetDeleteMessagesCommand(ulong GuildId, bool Enabled) : ICommand<string>;

public sealed class SetDeleteMessagesHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetDeleteMessagesCommand, string>
{
    public async ValueTask<string> Handle(SetDeleteMessagesCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetDeleteMessages(command.Enabled);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return command.Enabled ? "✅ Auto-delete spam enabled" : "❌ Auto-delete spam disabled";
    }
}

public sealed class SetDeleteMessagesEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/delete",
            async (SetDeleteMessagesCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
