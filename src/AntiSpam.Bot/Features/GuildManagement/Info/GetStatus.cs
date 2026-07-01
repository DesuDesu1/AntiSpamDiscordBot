using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using Discord.Rest;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.GuildManagement.Info;

public sealed record GetStatusCommand(ulong GuildId) : ICommand<string>;

public sealed class GetStatusHandler(BotDbContext db, DiscordRestClient discord)
    : ICommandHandler<GetStatusCommand, string>
{
    public async ValueTask<string> Handle(GetStatusCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.AsNoTracking().GetOrDefaultAsync(command.GuildId, ct);

        var alertChannelDisplay = "Not set";
        if (config.AlertChannelId.HasValue)
        {
            try
            {
                var guild = await discord.GetGuildAsync(command.GuildId);
                var channel = await guild.GetTextChannelAsync(config.AlertChannelId.Value);
                alertChannelDisplay = $"#{channel?.Name ?? config.AlertChannelId.Value.ToString()}";
            }
            catch
            {
                alertChannelDisplay = $"ID: {config.AlertChannelId.Value}";
            }
        }

        var allowedLinksDisplay = config.AllowedLinks.Count > 0 ? $"{config.AllowedLinks.Count} links" : "None";

        return $"""
            📊 **Anti-Spam Settings**

            Protection: {(config.IsEnabled ? "✅ Enabled" : "❌ Disabled")}
            Alert Channel: {alertChannelDisplay}

            **Detection:**
            • Min channels: {config.MinChannelsForSpam}
            • Text similarity: {config.SimilarityThreshold:P0}
            • Time window: {config.DetectionWindowSeconds} sec

            **New User Links:**
            • Detection: {(config.DetectNewUserLinks ? "✅ Enabled" : "❌ Disabled")}
            • New user threshold: {config.NewUserHoursThreshold}h
            • Allowed links: {allowedLinksDisplay}

            **Actions:**
            • Mute: {(config.MuteOnSpam ? $"✅ {config.MuteDurationMinutes} min" : "❌")}
            • Delete messages: {(config.DeleteMessages ? "✅" : "❌")}
            """;
    }
}

public sealed class GetStatusEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/status",
            async (GetStatusCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
