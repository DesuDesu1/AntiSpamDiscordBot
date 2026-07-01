using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Discord.Rest;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

/// <param name="Channel">Discord channel id (the `channel` slash option, resolved to its id by Gateway).</param>
public sealed record SetAlertChannelCommand(ulong GuildId, ulong Channel) : ICommand<string>;

public sealed class SetAlertChannelHandler(BotDbContext db, GuildConfigCache cache, DiscordRestClient discord)
    : ICommandHandler<SetAlertChannelCommand, string>
{
    public async ValueTask<string> Handle(SetAlertChannelCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetAlertChannel(command.Channel);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);

        try
        {
            var guild = await discord.GetGuildAsync(command.GuildId);
            var channel = await guild.GetTextChannelAsync(command.Channel);
            return $"✅ Alert channel set to #{channel?.Name ?? command.Channel.ToString()}";
        }
        catch
        {
            return $"✅ Alert channel set (ID: {command.Channel})";
        }
    }
}

public sealed class SetAlertChannelEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/alert-channel",
            async (SetAlertChannelCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
