using System.Net;
using Midjourney.Services;
using Xunit;

namespace Midjourney.Transport.Tests;

public class DiscordInteractionTransportTests
{
    private static HttpResponseMessage Response(int status, string body = "{}", string retry = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        if (retry != null) response.Headers.TryAddWithoutValidation("Retry-After", retry);
        return response;
    }

    [Fact]
    public async Task RetryWaitsForLongestServerDelayAndDisposesResponses()
    {
        var waits = new List<TimeSpan>();
        var calls = 0;
        var response = Response(429, "{\"retry_after\":2.75}", "1.5");
        var transport = new DiscordInteractionTransport((wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        var result = await transport.SendAsync(_ => Task.FromResult(++calls == 1 ? response : Response(204)),
            () => true, _ => Assert.Fail("Should not pause"));
        Assert.True(result.Success);
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(2.75), Assert.Single(waits));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QueuedSubmissionCannotBypassGlobalCooldown()
    {
        var cooling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var transport = new DiscordInteractionTransport(async (_, ct) => { cooling.SetResult(); await release.Task.WaitAsync(ct); });
        Task<HttpResponseMessage> Send(CancellationToken _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? Response(429, "{\"retry_after\":1,\"global\":true}") : Response(204));
        var first = transport.SendAsync(Send, () => true, _ => Assert.Fail("Should not pause"));
        await cooling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = transport.SendAsync(Send, () => true, _ => Assert.Fail("Should not pause"));
        Assert.Equal(1, calls);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), r => Assert.True(r.Success));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(400, "{\"captcha_key\":[\"required\"]}")]
    [InlineData(400, "{\"code\":40002}")]
    [InlineData(500, "{}")]
    [InlineData(408, "{}")]
    public async Task AuthChallengeAndUnknownOutcomePauseWithoutRetry(int status, string body)
    {
        var calls = 0;
        var pauses = 0;
        var transport = new DiscordInteractionTransport();
        Task<HttpResponseMessage> Send(CancellationToken _) { calls++; return Task.FromResult(Response(status, body)); }
        Assert.False((await transport.SendAsync(Send, () => true, _ => pauses++)).Success);
        Assert.False((await transport.SendAsync(Send, () => true, _ => pauses++)).Success);
        Assert.Equal(1, calls);
        Assert.Equal(1, pauses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResponseNeverResubmits(bool timeout)
    {
        var calls = 0;
        var pauses = 0;
        var transport = new DiscordInteractionTransport();
        Task<HttpResponseMessage> Send(CancellationToken _)
        {
            calls++;
            return Task.FromException<HttpResponseMessage>(timeout ? new TaskCanceledException() : new HttpRequestException());
        }
        var result = await transport.SendAsync(Send, () => true, _ => pauses++);
        Assert.Contains("提交结果未知", result.Description);
        await transport.SendAsync(Send, () => true, _ => pauses++);
        Assert.Equal(1, calls);
        Assert.Equal(1, pauses);
    }

    [Fact]
    public async Task ExhaustedRateLimitStopsAfterThreeAttempts()
    {
        var calls = 0;
        var waits = 0;
        var pauses = 0;
        var transport = new DiscordInteractionTransport((_, _) => { waits++; return Task.CompletedTask; });
        var result = await transport.SendAsync(_ => { calls++; return Task.FromResult(Response(429, "{\"retry_after\":1}")); },
            () => true, _ => pauses++);
        Assert.False(result.Success);
        Assert.Equal(3, calls);
        Assert.Equal(3, waits);
        Assert.Equal(1, pauses);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"retry_after\":-1}")]
    [InlineData("{\"retry_after\":301}")]
    [InlineData("{\"retry_after\":1e100}")]
    public async Task MissingOrLongCooldownPausesInsteadOfShorteningIt(string body)
    {
        var calls = 0;
        var pauses = 0;
        var transport = new DiscordInteractionTransport((_, _) => throw new Exception("Must not retry"));
        await transport.SendAsync(_ => { calls++; return Task.FromResult(Response(429, body)); }, () => true, _ => pauses++);
        Assert.Equal(1, calls);
        Assert.Equal(1, pauses);
    }

    [Fact]
    public void RetryAfterHttpDateAndMalformedJsonAreSupported()
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        using var response = Response(429, "bad json", now.AddSeconds(20).ToString("R"));
        Assert.Equal(TimeSpan.FromSeconds(20), DiscordInteractionTransport.RetryDelay(response, "bad json", now));
        Assert.False(DiscordInteractionTransport.RequiresVerification("{\"code\":\"unexpected\"}"));
    }

    [Fact]
    public async Task DisabledAccountDoesNotSend()
    {
        var result = await new DiscordInteractionTransport().SendAsync(_ => throw new Exception("Must not send"),
            () => false, _ => Assert.Fail("Already disabled"));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CancellationDuringCooldownPausesAndDoesNotResend()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var pauses = 0;
        var transport = new DiscordInteractionTransport((_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await transport.SendAsync(_ => { calls++; return Task.FromResult(Response(429, "{\"retry_after\":1}")); },
            () => true, _ => pauses++, cts.Token);
        Assert.Equal(1, calls);
        Assert.Equal(1, pauses);
    }

    [Fact]
    public async Task NotFoundIsNotReplayedWithAnotherMessageId()
    {
        var calls = 0;
        var result = await new DiscordInteractionTransport().SendAsync(_ => { calls++; return Task.FromResult(Response(404)); },
            () => true, _ => Assert.Fail("Should only reject this task"));
        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(4004, true)]
    [InlineData(4010, true)]
    [InlineData(4011, true)]
    [InlineData(4012, true)]
    [InlineData(4013, true)]
    [InlineData(4014, true)]
    [InlineData(4003, false)]
    [InlineData(4008, false)]
    [InlineData(1006, false)]
    public void TerminalGatewayErrorsRequireReview(int code, bool expected) =>
        Assert.Equal(expected, DiscordConnectionPolicy.RequiresManualReview(code));

    [Fact]
    public void ReconnectBackoffIsBounded() =>
        Assert.Equal(new double[] { 2, 4, 8, 16, 32, 60, 60 },
            Enumerable.Range(1, 7).Select(n => DiscordConnectionPolicy.ReconnectDelay(n).TotalSeconds));
}
