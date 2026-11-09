using System.Net;
using System.Net.Sockets;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The connect's first three requests report an unreachable server through whichever hears
/// it first, because under HTTP/2 they wait on one connection attempt at a time
/// (<see cref="TransportFailure"/>); any other failure of a companion is its own business.
/// </summary>
public class TransportFailureTests
{
    private static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it. (127.0.0.1:1)",
            new SocketException((int)SocketError.ConnectionRefused));

    [Test]
    public async Task The_main_result_comes_back_when_nothing_failed()
    {
        var main = new TaskCompletionSource<string>();
        var companion = new TaskCompletionSource<bool>();
        var first = TransportFailure.FirstOf(main.Task, companion.Task);

        companion.SetResult(true);
        main.SetResult("v1.33.4");

        await Assert.That(await first).IsEqualTo("v1.33.4");
    }

    [Test]
    public async Task A_companion_that_cannot_reach_the_server_fails_the_main_request_at_once()
    {
        var main = new TaskCompletionSource<string>();
        var companion = new TaskCompletionSource<bool>();
        var refused = Refused();
        var first = TransportFailure.FirstOf(main.Task, companion.Task);

        companion.SetException(refused);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => first);
        await Assert.That(thrown).IsSameReferenceAs(refused);
        await Assert.That(main.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task A_companion_the_server_refused_with_a_status_is_left_alone()
    {
        var main = new TaskCompletionSource<string>();
        var companion = new TaskCompletionSource<bool>();
        var first = TransportFailure.FirstOf(main.Task, companion.Task);

        companion.SetException(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));
        await Task.Yield();
        await Assert.That(first.IsCompleted).IsFalse();

        main.SetResult("v1.33.4");
        await Assert.That(await first).IsEqualTo("v1.33.4");
    }

    [Test]
    public async Task The_main_requests_own_failure_is_thrown_as_it_was()
    {
        var main = new TaskCompletionSource<string>();
        var companion = new TaskCompletionSource<bool>();
        var refused = Refused();
        var first = TransportFailure.FirstOf(main.Task, companion.Task);

        main.SetException(refused);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => first);
        await Assert.That(thrown).IsSameReferenceAs(refused);
    }

    [Test]
    public async Task A_wrapped_transport_failure_counts_and_a_protocol_error_does_not()
    {
        await Assert.That(TransportFailure.Is(new InvalidOperationException("wrapped", Refused()))).IsTrue();
        await Assert.That(TransportFailure.Is(new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"))).IsTrue();
        await Assert.That(TransportFailure.Is(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS"))).IsTrue();
        await Assert.That(TransportFailure.Is(new HttpRequestException(HttpRequestError.HttpProtocolError, "GOAWAY"))).IsFalse();
        await Assert.That(TransportFailure.Is(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden))).IsFalse();
        await Assert.That(TransportFailure.Is(new TaskCanceledException())).IsFalse();
    }
}
