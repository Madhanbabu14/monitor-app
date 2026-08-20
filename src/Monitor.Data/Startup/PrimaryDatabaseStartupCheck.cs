using Microsoft.Extensions.Hosting;
using Monitor.Data.Repositories;

namespace Monitor.Data.Startup;

/// <summary>
/// Fail-fast startup probe for the primary database. Analogue of the
/// source calling <c>testConnection()</c> before <c>app.listen()</c>: if the
/// primary database is unreachable, this throws during
/// <see cref="IHostedService.StartAsync"/>, which aborts host startup
/// instead of accepting traffic against a DB that can't be reached. This is
/// on top of (not a replacement for) <c>DatabaseOptions</c>' own
/// <c>ValidateOnStart</c>, which only fails fast on missing/invalid
/// *configuration* — this checks that the configured connection actually
/// works.
/// </summary>
public sealed class PrimaryDatabaseStartupCheck : IHostedService
{
    private readonly IPrimaryDb _primaryDb;

    public PrimaryDatabaseStartupCheck(IPrimaryDb primaryDb)
    {
        _primaryDb = primaryDb;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _primaryDb.TestConnectionAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
