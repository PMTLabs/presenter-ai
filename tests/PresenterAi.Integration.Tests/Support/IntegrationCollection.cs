using Xunit;

namespace PresenterAi.Integration.Tests.Support;

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "integration";
}
