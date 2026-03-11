using System.Threading.Channels;
using BasicGrpcService.Basic.Service.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Utils;

namespace Services;

/// <summary>
/// gRPC service implementation for <c>basic.v1.BasicService</c>.
/// </summary>
/// <remarks>
/// Provides three RPC methods that demonstrate different gRPC communication patterns:
/// <list type="bullet">
///   <item><description><see cref="Hello"/> — unary request/response.</description></item>
///   <item><description><see cref="Talk"/> — bidirectional streaming via an ELIZA chatbot.</description></item>
///   <item><description><see cref="Background"/> — server-side streaming using a fan-out/fan-in pattern.</description></item>
/// </list>
/// All responses are wrapped in a <c>CloudEvent</c> envelope.
/// </remarks>
public class BasicService : BasicGrpcService.Basic.V1.BasicService.BasicServiceBase
{
    /// <summary>
    /// Unary RPC that returns a personalised greeting wrapped in a Cloud Event.
    /// </summary>
    /// <param name="request">The incoming request containing the message to greet.</param>
    /// <param name="context">The gRPC server call context, used to build the Cloud Event source.</param>
    /// <returns>
    /// A <see cref="HelloResponse"/> whose <c>CloudEvent</c> payload is a packed
    /// <see cref="HelloResponseEvent"/> containing the greeting string.
    /// </returns>
    public override Task<HelloResponse> Hello(HelloRequest request, ServerCallContext context)
    {
        var protoData = Any.Pack(new HelloResponseEvent { Greeting = "Hello, " + request.Message });
        return Task.FromResult(new HelloResponse { CloudEvent = GeneratorUtils.CreateCloudEvent(context, protoData) });
    }

    /// <summary>
    /// Bidirectional streaming RPC that feeds each client message through an ELIZA chatbot
    /// and streams the reply back immediately.
    /// </summary>
    /// <remarks>
    /// A single <see cref="Eliza"/> instance is created per call so that conversation
    /// memory (the internal response-cycling state) is maintained across the lifetime
    /// of the stream.
    /// </remarks>
    /// <param name="requestStream">Async stream of <see cref="TalkRequest"/> messages from the client.</param>
    /// <param name="responseStream">Async stream used to write <see cref="TalkResponse"/> messages back to the client.</param>
    /// <param name="context">The gRPC server call context.</param>
    public override async Task Talk(
        IAsyncStreamReader<TalkRequest> requestStream,
        IServerStreamWriter<TalkResponse> responseStream,
        ServerCallContext context)
    {
        var eliza = new Eliza();

        await foreach (var request in requestStream.ReadAllAsync())
        {
            var response = new TalkResponse { Answer = eliza.Reply(request.Message) };
            await responseStream.WriteAsync(response);
        }
    }

    /// <summary>
    /// Server streaming RPC that runs <see cref="BackgroundRequest.Processes"/> fake service calls
    /// concurrently and streams a cumulative status event to the client as each one completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses a <b>fan-out/fan-in</b> pattern backed by an unbounded <see cref="Channel{T}"/>:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Fan-out</b> — all processes are started concurrently via <see cref="RunProcess"/>.
    ///     Each writes its raw <see cref="SomeServiceResponse"/> result (or <see langword="null"/> on failure)
    ///     into the channel the moment it finishes.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Fan-in</b> — a single sequential loop drains the channel. Because only this loop
    ///     mutates the shared <see cref="BackgroundResponseEvent"/>, no locking is required.
    ///     Every result is appended to <c>Responses</c> and the updated event is streamed
    ///     immediately, so the client observes the list grow in completion order rather than
    ///     submission order.
    ///   </description></item>
    /// </list>
    /// <para>
    /// A final event is always sent once the channel is drained. Its <c>State</c> is set to
    /// <see cref="State.Complete"/> when all processes succeeded, or
    /// <see cref="State.CompleteWithError"/> if at least one failed.
    /// </para>
    /// </remarks>
    /// <param name="request">
    /// The incoming request specifying how many parallel processes to spawn via
    /// <see cref="BackgroundRequest.Processes"/>.
    /// </param>
    /// <param name="responseStream">Async stream used to write <see cref="BackgroundResponse"/> events to the client.</param>
    /// <param name="context">The gRPC server call context, used for cancellation and Cloud Event metadata.</param>
    public override async Task Background(
        BackgroundRequest request,
        IServerStreamWriter<BackgroundResponse> responseStream,
        ServerCallContext context)
    {
        var startedAt = Timestamp.FromDateTime(DateTime.UtcNow);

        // Channel now carries the raw result of each process, not a full response.
        // null signals that a process failed.
        var channel = Channel.CreateUnbounded<SomeServiceResponse?>();

        // Fan-out: all processes start at the same time.
        var processes = Enumerable
            .Range(0, (int)request.Processes)
            .Select(i => RunProcess(i, channel.Writer, context.CancellationToken))
            .ToList();

        _ = Task.WhenAll(processes).ContinueWith(
            t => channel.Writer.Complete(t.IsFaulted ? t.Exception : null),
            TaskScheduler.Default);

        // Single shared event that accumulates responses over time.
        var responseEvent = new BackgroundResponseEvent
        {
            State = State.Process,
            StartedAt = startedAt,
        };

        var hasErrors = false;

        // Fan-in: the loop is sequential, so .Add() is safe without any lock.
        // Each result that arrives is appended and the updated event is streamed
        // immediately — the client sees responses grow in completion order.
        await foreach (var result in channel.Reader.ReadAllAsync(context.CancellationToken))
        {
            if (result is null)
            {
                hasErrors = true;
                continue;
            }

            responseEvent.Responses.Add(result);

            await responseStream.WriteAsync(
                new BackgroundResponse { CloudEvent = GeneratorUtils.CreateCloudEvent(context, Any.Pack(responseEvent)) },
                context.CancellationToken);
        }

        // Final event: all processes done, state reflects whether any failed.
        responseEvent.State = hasErrors ? State.CompleteWithError : State.Complete;
        responseEvent.CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow);

        await responseStream.WriteAsync(
            new BackgroundResponse { CloudEvent = GeneratorUtils.CreateCloudEvent(context, Any.Pack(responseEvent)) },
            context.CancellationToken);
    }

    /// <summary>
    /// Executes a single fake service call and writes the result into the shared channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service type is rotated through a fixed list based on <paramref name="index"/> so that
    /// each process in the same <see cref="Background"/> call simulates a different transport
    /// (e.g. <c>rpc</c>, <c>grpc</c>, <c>rest</c>).
    /// </para>
    /// <para>
    /// On failure, <see langword="null"/> is written to <paramref name="writer"/> instead of
    /// propagating the exception. This keeps the other in-flight processes running and lets
    /// the fan-in loop in <see cref="Background"/> record the failure via <c>hasErrors</c>
    /// without aborting the entire stream. Cancellation is intentionally not caught so that
    /// client disconnects terminate all processes cleanly.
    /// </para>
    /// </remarks>
    /// <param name="index">Zero-based process index, used as the service name suffix and to rotate the service type.</param>
    /// <param name="writer">The channel writer shared across all concurrent processes.</param>
    /// <param name="ct">Cancellation token propagated from the parent <see cref="Background"/> call.</param>
    private static async Task RunProcess(
        int index,
        ChannelWriter<SomeServiceResponse?> writer,
        CancellationToken ct)
    {
        List<string> types = ["rpc", "grpc", "file", "rest", "mail", "graphql"];

        try
        {
            var result = await GeneratorUtils.FakeCall("service-" + index, types[index % types.Count]);
            await writer.WriteAsync(result, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await writer.WriteAsync(null, ct);
        }
    }
}
