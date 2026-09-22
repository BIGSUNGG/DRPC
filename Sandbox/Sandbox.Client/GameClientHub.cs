using DRPC.Client.Network;
using Sandbox.Contracts;

namespace Sandbox.Client;

/// <summary>
/// Client-side hub. Server contracts (<see cref="IGameServerProcedures"/>) are called through the generated <c>*Async</c> stubs,
/// while client contracts (<see cref="IGameClientProcedures"/>) are implemented as <c>*_Implementation</c> partials.
/// </summary>
public partial class GameClientHub : ClientHub<IGameServerProcedures, IGameClientProcedures>
{
    private partial Task<float> EchoSum_Implementation(List<float> values)
    {
        Console.WriteLine($"[client] EchoSum called with {values.Count} values");
        return Task.FromResult(values.Sum());
    }

    private partial Task<int> CountConfig_Implementation(string? label, int[] values)
    {
        Console.WriteLine($"[client] CountConfig called: label={(label ?? "<null>")}, values={values.Length}");
        return Task.FromResult(values.Length);
    }

    private partial Task NotifyScore_Implementation(ScoreBoard score)
    {
        Console.WriteLine($"[client] NotifyScore(one-way): map={score.Map}, lines={score.Lines.Count}, first={score.Lines[0].Score}");
        return Task.CompletedTask;
    }
}
