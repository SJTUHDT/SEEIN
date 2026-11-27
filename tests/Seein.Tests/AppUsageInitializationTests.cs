using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Core.Storage;
using Seein.Service;

namespace Seein.Tests;

public sealed class AppUsageInitializationTests
{
    [Fact]
    public async Task InitializationCanRetryAfterDatabasePathBecomesAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "app-usage.db");
        Directory.CreateDirectory(databasePath);
        try
        {
            await using var repository = new AppUsageRepository(databasePath);
            await using var usage = new AppUsageService(NullLogger<AppUsageService>.Instance, new PanelState(), repository);
            await Assert.ThrowsAnyAsync<Exception>(() => usage.EnsureInitializedAsync(CancellationToken.None));
            Directory.Delete(databasePath);
            await usage.EnsureInitializedAsync(CancellationToken.None);
            Assert.True(File.Exists(databasePath));
        }
        finally { Directory.Delete(root, true); }
    }
}
