using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PresenterAi.Infrastructure.Persistence;

public sealed class PresenterAiDbContextFactory : IDesignTimeDbContextFactory<PresenterAiDbContext>
{
    public PresenterAiDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5433;Database=presenter_ai;Username=presenter;Password=dev_password_change_me";

        var options = new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PresenterAiDbContext(options);
    }
}
