using Azure.Core;
using Azure.Core.Pipeline;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// #525 S-06 fault-injection seam. Per the Issue #525 instructions: when a real fault cannot be
/// injected against the live provider network path itself, use an SDK transport seam that can
/// still clearly distinguish "before submission" from "after submission" — never a fixture that
/// blurs the two.
///
/// Wraps the real <see cref="HttpClientTransport"/> and forwards every request to it unchanged
/// (so the HTTP request genuinely reaches Azure Communication Services and Azure's real response
/// is genuinely received over the wire). Only AFTER the inner transport has fully completed
/// processing the FIRST request (the initial send submission) does this throw, simulating a local
/// fault that occurs after the provider has already accepted the request but before the caller
/// can observe the outcome — i.e. a true unknown_after_submission window, not
/// definitely_not_submitted. Every call after the first (e.g. any subsequent LRO polling GET)
/// passes through untouched.
/// </summary>
internal sealed class Spike525AcsFaultAfterSubmissionTransport : HttpPipelineTransport
{
    private readonly HttpClientTransport _inner = new();
    private int _callCount;

    internal int CallCount => _callCount;

    public override Request CreateRequest() => _inner.CreateRequest();

    public override void Process(HttpMessage message)
    {
        var call = Interlocked.Increment(ref _callCount);
        _inner.Process(message);
        if (call == 1)
        {
            throw new IOException("spike525-s06: injected local fault after the real ACS response was already received on the wire.");
        }
    }

    public override async ValueTask ProcessAsync(HttpMessage message)
    {
        var call = Interlocked.Increment(ref _callCount);
        await _inner.ProcessAsync(message);
        if (call == 1)
        {
            throw new IOException("spike525-s06: injected local fault after the real ACS response was already received on the wire.");
        }
    }
}
