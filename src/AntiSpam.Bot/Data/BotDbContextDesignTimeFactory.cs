using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AntiSpam.Bot.Data;


public sealed class BotDbContextDesignTimeFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseNpgsql("Host=localhost;Database=antispam;Username=antispam")
            .Options;
        return new BotDbContext(options);
    }
}
