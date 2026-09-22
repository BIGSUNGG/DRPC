using Communication.Network.RUDP;   // RudpTlsOptions — DTLS certificate pinning
using DRPC.Shared;                  // RpcFaultException
using DRPC.Shared.Message;
using DRPC.Shared.Network;
using Sandbox.Client;
using Sandbox.Contracts;

const string ConnectionKey = "sandbox-key";

// --tls <fingerprint>: pin-verify the server certificate against the SHA-256 fingerprint the server printed via --tls. Without this argument, connect in plaintext.
string? fingerprint = args.Length >= 2 && args[0] == "--tls" ? args[1] : null;

var clientOptions = new RpcEndpointOptions { ConnectionKey = ConnectionKey };
if (fingerprint is not null)
{
    Console.WriteLine($"[client] Expected DTLS pin fingerprint: {fingerprint}");
    string expected = fingerprint.ToLowerInvariant();
    clientOptions.TlsCertificateValidation = der => RudpTlsOptions.GetSha256Fingerprint(der) == expected;
}

using var hub = await GameClientHub.ConnectAsync("127.0.0.1", 9050, clientOptions);
Console.WriteLine($"[client] connected{(fingerprint is not null ? " (DTLS 1.2)" : "")}");

// 1) Basic ReliableOrdered call
Console.WriteLine($"[client] Add(2, 3) -> {await hub.AddAsync(2, 3)}");

// 2) Message-typed parameter and return value
PlayerJoined joined = await hub.JoinAsync(new Player { Id = 7, Name = "Hong" });
Console.WriteLine($"[client] Join -> playerId={joined.PlayerId} roomId={joined.RoomId}");

// 3) Delivery-mode override (Sequenced) — a single attribute changes how the call is delivered
await hub.SetPositionAsync(7, 1.25f, -3.5f);
Console.WriteLine("[client] SetPosition(Sequenced) sent");

// 4) OneWay — delivered without a response
await hub.LogChatAsync("hello from sandbox");
Console.WriteLine("[client] LogChat(OneWay) sent");

// 5) Group polymorphism — send a derived type under the root-type contract
await hub.ChatMessageAsync(new ShoutChatLine { Text = "gg" });
Console.WriteLine("[client] ChatMessage(OneWay, actual type ShoutChatLine) sent");

// 6) Generic ① — return-only: explicit type argument. The allowed set is declared via [GenericProcedure] (int/string).
Console.WriteLine($"[client] GetConfig<int>() -> {await hub.GetConfigAsync<int>()}");
Console.WriteLine($"[client] GetConfig<string>() -> {await hub.GetConfigAsync<string>()}");

// 7) Generic ② — parameter generic: call it like a normal method without type arguments (T is inferred).
Console.WriteLine($"[client] Describe(7) -> {await hub.DescribeAsync(7)}");
Console.WriteLine($"[client] Describe(\"gg\") -> {await hub.DescribeAsync("gg")}");

// 8) Generic ③ — multiple slots combined (Cartesian product of configurations).
Console.WriteLine($"[client] Blend<int, float, Player>(1.5f, player) -> {await hub.BlendAsync<int, float, Player>(1.5f, new Player { Id = 1, Name = "Hong" })}");
Console.WriteLine($"[client] Blend<string, double, ChatLine>(2.5, chat) -> {await hub.BlendAsync<string, double, ChatLine>(2.5, new ShoutChatLine { Text = "hi" })}");

// 9) Generic ④ — [GenericMessage] parameter: T's allowed set is inherited from the GiftBox configuration declarations.
await hub.UnwrapAsync(new GiftBox<ChatLine> { Gift = new ShoutChatLine { Text = "boxed" } });
await hub.UnwrapAsync(new GiftBox<Token> { Gift = new Token { Value = 7 } });
Console.WriteLine("[client] Unwrap(GiftBox<ChatLine>/GiftBox<Token>) sent");

// 10) F14 validation gate — only calls that pass _Validate reach the implementation.
Console.WriteLine($"[client] TransferGold(7, 8, 100) -> {await hub.TransferGoldAsync(7, 8, 100)}");

try
{
    await hub.TransferGoldAsync(7, 7, -50); // amount <= 0 — _Validate false → server implementation not invoked
}
catch (RpcFaultException fault) when (fault.ErrorCode == RpcErrorCode.ValidationFailed)
{
    Console.WriteLine($"[client] TransferGold(7, 7, -50) rejected: ErrorCode={fault.ErrorCode} ({fault.Message})");
}

// 11) Wait briefly so the receiving implementation has time to process the peer's one-way call.
await Task.Delay(500);

hub.Disconnect();
Console.WriteLine("[client] disconnected");
