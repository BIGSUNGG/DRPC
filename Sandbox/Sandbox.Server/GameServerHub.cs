using DRPC.Server.Network;
using Sandbox.Contracts;

namespace Sandbox.Server;

/// <summary>
/// Server-side hub. Just fill in a <c>{Name}_Implementation</c> partial for each contract method —
/// transport, serialization, CallId, and routing are all handled by the generated code.
/// </summary>
public partial class GameServerHub : ServerHub<IGameServerProcedures, IGameClientProcedures>
{
    /// <summary>A declaration with only [RemoteProcedure] defaults to ReliableOrdered.</summary>
    private partial Task<int> Add_Implementation(int value1, int value2)
        => Task.FromResult(value1 + value2);

    private partial Task<PlayerJoined> Join_Implementation(Player player)
    {
        Console.WriteLine($"[server] Join from player {player.Id} ({player.Name})");
        return Task.FromResult(new PlayerJoined { PlayerId = player.Id, RoomId = 100 });
    }

    /// <summary>State update arriving over Sequenced delivery (tolerates loss and out-of-order frames).</summary>
    private partial Task SetPosition_Implementation(int playerId, float x, float y)
    {
        Console.WriteLine($"[server] SetPosition player={playerId} pos=({x}, {y})");
        return Task.CompletedTask;
    }

    /// <summary>OneWay, so no response is sent.</summary>
    private partial Task LogChat_Implementation(string text)
    {
        Console.WriteLine($"[server] chat: {text}");
        return Task.CompletedTask;
    }

    /// <summary>Group polymorphism: the actual type (ShoutChatLine) is preserved on arrival.</summary>
    private partial Task ChatMessage_Implementation(ChatLine line)
    {
        Console.WriteLine($"[server] {line.Describe()} ({line.GetType().Name})");
        return Task.CompletedTask;
    }

    /// <summary>Generic ①: the T slot is constrained to the allowed set (int/string) at both compile time and runtime.</summary>
    private partial Task<T> GetConfig_Implementation<T>()
        => Task.FromResult<T>(typeof(T) == typeof(int) ? (T)(object)42 : (T)(object)"default");

    /// <summary>Generic ②: called like a normal method via caller-side type inference.</summary>
    private partial Task<string> Describe_Implementation<T>(T value)
        => Task.FromResult($"{typeof(T).Name}={value}");

    private partial Task<T1> Blend_Implementation<T1, T2, T3>(T2 left, T3 right)
    {
        Console.WriteLine($"[server] Blend<{typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}> left={left} right={right?.GetType().Name ?? "null"}");
        return Task.FromResult<T1>(default!);
    }

    /// <summary>Generic ④: the [GenericMessage] configuration (ClassId) identifies T on the wire.</summary>
    private partial Task Unwrap_Implementation<T>(GiftBox<T> box)
    {
        string detail = box.Gift switch { ChatLine c => c.Text, Token t => $"token#{t.Value}", _ => "?" };
        Console.WriteLine($"[server] Unwrap<{typeof(T).Name}>: {detail}");
        return Task.CompletedTask;
    }

    /// <summary>F14 validation gate: runs before the implementation. On false, TransferGold_Implementation
    /// never executes and the hub replies with an RpcErrorCode.ValidationFailed(7) fault (observed as RpcFaultException on the client).</summary>
    private partial Task<bool> TransferGold_Validate(int fromPlayer, int toPlayer, int amount)
    {
        bool pass = amount > 0 && fromPlayer != toPlayer;
        Console.WriteLine($"[server] TransferGold_Validate from={fromPlayer} to={toPlayer} amount={amount} -> {(pass ? "pass" : "reject")}");
        return Task.FromResult(pass);
    }

    private partial Task<int> TransferGold_Implementation(int fromPlayer, int toPlayer, int amount)
    {
        Console.WriteLine($"[server] TransferGold_Implementation {fromPlayer}->{toPlayer} x{amount}");
        return Task.FromResult(amount);
    }
}
