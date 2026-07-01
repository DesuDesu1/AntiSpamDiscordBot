using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

/// <param name="Duration">Mute length in minutes (the optional `duration` slash option; defaults to 60 when omitted).</param>
public sealed record SetMuteSettingsCommand(ulong GuildId, bool Enabled, int Duration = 60) : ICommand<string>;

public sealed class SetMuteSettingsHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetMuteSettingsCommand, string>
{
    public async ValueTask<string> Handle(SetMuteSettingsCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetMuteSettings(command.Enabled, command.Duration);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return command.Enabled ? $"✅ Mute enabled ({command.Duration} min)" : "❌ Mute disabled";
    }
}

public sealed class SetMuteSettingsEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/mute",
            async (SetMuteSettingsCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
