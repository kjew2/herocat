using HeroCat.Server.Hosting;

namespace HeroCat.Server;

/// <summary>
/// Entry point of the one server image (specs/08 section 9.1): <c>HeroCat.Server</c> runs the api,
/// <c>HeroCat.Server --worker</c> the worker, <c>HeroCat.Server --migrate</c> the SQL migration runner.
/// </summary>
public sealed class Program
{
    public static Task<int> Main(string[] args) => ServerHost.RunAsync(args);
}
