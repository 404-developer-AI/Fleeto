namespace Fleeto.Infrastructure.Hosting;

/// <summary>
/// Liveness for components without a listening port (the signer, the workers). The service touches the file
/// on every healthy loop; the container health check runs <c>healthcheck</c> which checks its age.
/// </summary>
public static class HeartbeatFile
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    public static string PathFor(FleetoComponent component) =>
        Path.Combine(Path.GetTempPath(), $"fleeto-{component.ToString().ToLowerInvariant()}.heartbeat");

    public static void Touch(FleetoComponent component)
    {
        try
        {
            File.WriteAllText(PathFor(component), DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // Liveness only; never let it break the service.
        }
    }

    /// <summary>Exit code for the container health check: 0 healthy, 1 stale or missing.</summary>
    public static int Check(FleetoComponent component)
    {
        var path = PathFor(component);
        return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge ? 0 : 1;
    }
}
