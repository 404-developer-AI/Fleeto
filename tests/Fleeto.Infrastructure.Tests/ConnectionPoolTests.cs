using Fleeto.Infrastructure.Hosting;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The connection pools of an instance fit its PostgreSQL (0.3.0 step 7): the components that run together, each with its notification
/// listener, and the nightly backup stay inside the 100 connections minus the 3 kept for the superuser. Before, every pool could open 50
/// and a relay under load met "too many clients".
/// </summary>
public class ConnectionPoolTests
{
    private const int MaxConnections = 100;
    private const int SuperuserReserved = 3;

    [Fact]
    public void The_pools_of_the_running_components_fit_the_database()
    {
        FleetoComponent[] running = [FleetoComponent.Web, FleetoComponent.Gateway, FleetoComponent.Workers, FleetoComponent.Signer];
        var pools = running.Sum(c => c.DefaultPoolSize());
        var listeners = running.Length;
        const int backup = 1;
        Assert.True(pools + listeners + backup <= MaxConnections - SuperuserReserved,
            $"{pools} pooled + {listeners} listeners + {backup} backup exceed {MaxConnections - SuperuserReserved} connections");
        Assert.All(running, c => Assert.True(c.DefaultPoolSize() >= 5, $"{c} has too small a pool"));
    }
}
