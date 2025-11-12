#if DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.UnitTests.Connections;

[TestFixture]
public sealed class ResilientOutboundConnectionTests
{
    [Test]
    public async Task FullyReleaseReconnectGate_AllowsRepeatedAttempts_WhenMaxAttemptsIsOne()
    {
        var options = new ResilientSessionOptions
        {
            MaximalReconnectAttempts = 1,
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 12345);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        connection.GetReconnectGateCurrentCountForTesting().Should().Be(1);

        using var firstWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(firstWaitCts.Token).ConfigureAwait(false);

        connection.GetReconnectGateCurrentCountForTesting().Should().Be(0);

        var firstRefillSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);

        connection.FullyReleaseReconnectGateForTesting();

        await firstRefillSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        connection.GetReconnectGateCurrentCountForTesting().Should().Be(1);

        using var secondWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(secondWaitCts.Token).ConfigureAwait(false);

        connection.GetReconnectGateCurrentCountForTesting().Should().Be(0);

        var secondRefillSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);

        connection.FullyReleaseReconnectGateForTesting();

        await secondRefillSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        connection.GetReconnectGateCurrentCountForTesting().Should().Be(1);
    }

    [Test]
    public async Task FullyReleaseReconnectGate_SignalsOnlyWhenGateRefilled()
    {
        var options = new ResilientSessionOptions
        {
            MaximalReconnectAttempts = 1,
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 12345);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        var refillSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);

        connection.FullyReleaseReconnectGateForTesting();
        refillSignal.IsCompleted.Should().BeFalse();

        using var firstWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(firstWaitCts.Token).ConfigureAwait(false);

        connection.FullyReleaseReconnectGateForTesting();
        await refillSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var nextSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);
        nextSignal.IsCompleted.Should().BeFalse();

        using var secondWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(secondWaitCts.Token).ConfigureAwait(false);

        connection.FullyReleaseReconnectGateForTesting();
        await nextSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }
}
#endif
