using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Midjourney.Services;

/// <summary>
/// Serializes interaction submissions within one Discord service instance.
/// Only an explicit 429 can be retried: a lost response is not proof of rejection.
/// </summary>
public sealed class DiscordInteractionTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _paused;

    public DiscordInteractionTransport(
        Func<TimeSpan, CancellationToken, Task> delay = null,
        Func<DateTimeOffset> utcNow = null)
    {
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DiscordSubmissionResult> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<bool> canSubmit,
        Action<string> pause,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_paused || !canSubmit())
                    return new(false, 0, "账号已暂停或锁定，未发送请求");

                HttpResponseMessage response;
                try
                {
                    response = await send(cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    // Cancellation after dispatch may also mean Discord accepted the task.
                    return Pause("提交结果未知：请核对 Discord 消息后人工恢复，禁止自动重发", 0);
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.NoContent)
                        return new(true, status, "成功");

                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        return Pause($"Discord HTTP {status}：鉴权或权限失败，请人工检查后恢复", status);

                    if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                        return Pause($"Discord HTTP {status}：提交结果未知，请核对后恢复，禁止自动重发", status);

                    string body;
                    try
                    {
                        body = await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                    {
                        return Pause("Discord 错误响应未读取完整，请人工检查后恢复", status);
                    }

                    if (RequiresVerification(body))
                        return Pause("Discord 要求人工验证，请在官方客户端处理后恢复", status);

                    if (response.StatusCode != HttpStatusCode.TooManyRequests)
                        return new(false, status, $"Discord HTTP {status}：请求被拒绝，未重试");

                    var wait = RetryDelay(response, body, _utcNow());
                    if (wait == null || wait > TimeSpan.FromMinutes(5))
                        return Pause("Discord 限流等待时间缺失或超过 5 分钟，请人工检查后恢复", status);

                    // Keep the dispatch gate throughout cooldown, including the last attempt.
                    // Never shorten the server's requested wait or let queued submissions bypass it.
                    try
                    {
                        await _delay(wait.Value, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return Pause("Discord 限流等待被中断，请人工检查后恢复", status);
                    }
                }
            }

            return Pause("Discord 连续限流，已停止提交，请人工检查后恢复", 429);
        }
        finally
        {
            _gate.Release();
        }

        DiscordSubmissionResult Pause(string reason, int status)
        {
            _paused = true; // Fail closed even if persistence/notification throws.
            pause(reason);
            return new(false, status, reason);
        }
    }

    public static bool RequiresVerification(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("captcha_key", out _) ||
                 root.TryGetProperty("captcha_sitekey", out _) ||
                 (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                  code.TryGetInt32(out var value) && value == 40002));
        }
        catch (JsonException) { return false; }
    }

    public static TimeSpan? RetryDelay(HttpResponseMessage response, string body, DateTimeOffset now)
    {
        double? seconds = null;
        void Include(double value)
        {
            if (double.IsFinite(value) && value >= 0)
                seconds = Math.Max(seconds ?? 0, value);
        }

        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var value in values)
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    Include(number);
                else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date))
                    Include(Math.Max(0, (date - now).TotalSeconds));
            }
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("retry_after", out var retry) &&
                retry.ValueKind == JsonValueKind.Number && retry.TryGetDouble(out var number))
                Include(number);
        }
        catch (JsonException) { }

        // Values too large to represent are a manual-review case, never an immediate retry.
        if (seconds == null) return null;
        if (seconds > 300) return TimeSpan.MaxValue;
        return TimeSpan.FromSeconds(Math.Max(1, seconds.Value));
    }
}

public sealed record DiscordSubmissionResult(bool Success, int StatusCode, string Description);

public static class DiscordConnectionPolicy
{
    // Discord marks these gateway close codes as non-reconnectable.
    public static bool RequiresManualReview(int code) => code is 4004 or 4010 or 4011 or 4012 or 4013 or 4014;

    public static TimeSpan ReconnectDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 5))));
}
