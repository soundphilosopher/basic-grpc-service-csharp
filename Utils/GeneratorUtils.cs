using BasicGrpcService.Basic.Service.V1;
using BasicGrpcService.CloudEvents.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Utils;

/// <summary>
/// Static factory helpers shared across service implementations for building
/// Cloud Event envelopes and simulating fake external service calls.
/// </summary>
public sealed partial class GeneratorUtils
{
    /// <summary>
    /// Wraps a packed protobuf payload in a <see cref="CloudEvent"/> envelope
    /// conforming to the CloudEvents specification v1.0.
    /// </summary>
    /// <remarks>
    /// The following fields are populated automatically:
    /// <list type="table">
    ///   <listheader><term>Field</term><description>Value</description></listheader>
    ///   <item>
    ///     <term><c>id</c></term>
    ///     <description>A freshly generated <see cref="Guid"/> — unique per event.</description>
    ///   </item>
    ///   <item>
    ///     <term><c>specversion</c></term>
    ///     <description>Hardcoded to <c>"1.0"</c> (the CloudEvents specification version).</description>
    ///   </item>
    ///   <item>
    ///     <term><c>type</c></term>
    ///     <description>
    ///       Taken from <see cref="Any.TypeUrl"/> of <paramref name="protoData"/>, which is the
    ///       fully-qualified protobuf type URL (e.g. <c>type.googleapis.com/basic.service.v1.HelloResponseEvent</c>).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term><c>source</c></term>
    ///     <description>
    ///       Set to <see cref="ServerCallContext.Method"/>, the fully-qualified gRPC method path
    ///       (e.g. <c>/basic.v1.BasicService/Hello</c>), identifying which RPC produced the event.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term><c>data</c></term>
    ///     <description>The <paramref name="protoData"/> payload passed by the caller.</description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="context">
    /// The active gRPC server call context. Only <see cref="ServerCallContext.Method"/> is read;
    /// the context is not mutated.
    /// </param>
    /// <param name="protoData">
    /// A protobuf message packed into a <see cref="Any"/> wrapper via <see cref="Any.Pack"/>.
    /// Its <see cref="Any.TypeUrl"/> is used as the event <c>type</c>.
    /// </param>
    /// <returns>A fully populated <see cref="CloudEvent"/> ready to be set on a response message.</returns>
    public static CloudEvent CreateCloudEvent(ServerCallContext context, Any protoData)
    {
        return new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            SpecVersion = "1.0",
            Type = protoData.TypeUrl,
            Source = context.Method,
            ProtoData = protoData,
        };
    }

    /// <summary>
    /// Simulates an asynchronous call to an external service by waiting for a random
    /// delay and returning a stub <see cref="SomeServiceResponse"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delay is implemented with <see cref="Task.Delay(int)"/> rather than
    /// <see cref="Thread.Sleep(int)"/> so that the calling thread is released back to
    /// the thread pool while waiting. This is critical for the fan-out pattern in
    /// <c>BasicService.Background</c>, where many calls run concurrently — a blocking
    /// sleep would pin a thread per process, eliminating any concurrency benefit.
    /// </para>
    /// <para>
    /// <see cref="Random.Shared"/> is used instead of a local <see cref="Random"/> instance
    /// because it is thread-safe and avoids the seed-collision problem that occurs when
    /// multiple <c>new Random()</c> instances are created in rapid succession.
    /// </para>
    /// </remarks>
    /// <param name="name">
    /// The service name to embed in the response (e.g. <c>"service-0"</c>).
    /// Passed through directly to <see cref="SomeServiceResponse.Name"/>.
    /// </param>
    /// <param name="type">
    /// The transport type label to embed in the response payload
    /// (e.g. <c>"grpc"</c>, <c>"rest"</c>, <c>"rpc"</c>).
    /// Passed through directly to <see cref="SomeServiceData.Type"/>.
    /// </param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes after a random delay between
    /// 500 ms and 1 400 ms, yielding a stub <see cref="SomeServiceResponse"/> with
    /// a new <see cref="Guid"/> as its <c>Id</c> and <c>"some data"</c> as its value.
    /// </returns>
    public static async Task<SomeServiceResponse> FakeCall(string name, string type)
    {
        // Task.Delay releases the thread while waiting — Thread.Sleep would
        // block it, defeating the purpose of concurrent fan-out.
        await Task.Delay(Random.Shared.Next(500, 1400));

        return new SomeServiceResponse
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Version = "1.0",
            Data = new SomeServiceData { Type = type, Value = "some data" },
        };
    }
}
