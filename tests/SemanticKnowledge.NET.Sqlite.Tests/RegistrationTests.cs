using Microsoft.Extensions.DependencyInjection;
using SemanticKnowledge.Sqlite;

namespace SemanticKnowledge.Sqlite.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void Sqlite_registration_is_composable()
    {
        var services = new ServiceCollection();
        var builder = services.AddSemanticKnowledge().UseSqlite(":memory:");
        Assert.NotNull(builder);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IKnowledgeStorageProvider));
    }
}
