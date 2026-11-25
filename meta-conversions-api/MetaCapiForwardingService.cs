using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stobio.Application.MetaForwarding;

namespace Stobio.Infrastructure.MetaForwarding;

/// <summary>
/// Dispatches Meta Conversions API requests with retry classification mirroring the GA4
/// forwarder (4xx → non-retryable; 5xx / timeout / connection → retryable up to a small cap).
/// PixelId / AccessToken never logged in plain.
/// </summary>
public sealed class MetaCapiForwardingService : IMetaCapiForwardingService
{
    private const string EndpointTemplate = "https://graph.facebook.com/v25.0/{0}/events";
    private const int HttpTimeoutSeconds = 10;
    private const int MaxRetryCount = 2;
    private const int BaseRetryDelayMs = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetaCapiForwardingService> _logger;

    public MetaCapiForwardingService(
        IHttpClientFactory httpClientFactory,
        ILogger<MetaCapiForwardingService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MetaForwardingResult> ForwardAsync(MetaForwardingInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var url = string.Format(EndpointTemplate, Uri.EscapeDataString(input.PixelId));
        var retryCount = 0;

        for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient("MetaCapi");
                client.Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds);

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(input.RequestBodyJson, Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    if (retryCount > 0)
                    {
                        _logger.LogDebug("Meta CAPI forwarded after {RetryCount} retries. PixelId: {PixelId}",
                            retryCount, MaskPixelId(input.PixelId));
                    }
                    await LogResponseSummaryAsync(response, input, ct).ConfigureAwait(false);
                    return MetaForwardingResult.Success(retryCount);
                }

                var (reason, isRetryable) = Classify(response.StatusCode);
                if (!isRetryable || attempt >= MaxRetryCount)
                {
                    var metaErrorCode = await LogResponseSummaryAsync(response, input, ct).ConfigureAwait(false);
                    if (reason == MetaForwardingFailureReason.InvalidRequest4xx && IsMetaRateLimitCode(metaErrorCode))
                    {
                        reason = MetaForwardingFailureReason.RateLimited;
                    }
                    if (reason == MetaForwardingFailureReason.RateLimited)
                    {
                        // Retry-After is only logged; the wait is ForwardingRetryJob's existing
                        // backoff. Whether the header actually arrives is tracked from this log.
                        _logger.LogWarning(
                            "Meta CAPI rate limited. PixelId={PixelId} Status={Status} RetryAfter={RetryAfter}",
                            MaskPixelId(input.PixelId), (int)response.StatusCode,
                            response.Headers.RetryAfter?.ToString() ?? "none");
                    }
                    return MetaForwardingResult.Failed(reason,
                        $"Meta returned {(int)response.StatusCode} ({response.StatusCode}). PixelId: {MaskPixelId(input.PixelId)}",
                        retryCount);
                }

                retryCount++;
                await Task.Delay(BaseRetryDelayMs * (1 << Math.Min(attempt, 8)), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken != ct)
            {
                if (attempt >= MaxRetryCount)
                {
                    // Transient timeout — ForwardingRetryJob absorbs it. Warning is visible in logs,
                    // not pushed to Sentry (min Error).
                    _logger.LogWarning(
                        "Meta CAPI request timed out after {RetryCount} retries. PixelId: {PixelId}",
                        retryCount, MaskPixelId(input.PixelId));
                    return MetaForwardingResult.Failed(MetaForwardingFailureReason.NetworkTimeout,
                        $"Meta request timed out. PixelId: {MaskPixelId(input.PixelId)}", retryCount);
                }
                retryCount++;
                await Task.Delay(BaseRetryDelayMs * (1 << Math.Min(attempt, 8)), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxRetryCount)
                {
                    return MetaForwardingResult.Failed(MetaForwardingFailureReason.ConnectionError,
                        $"Meta request failed: {ex.Message}. PixelId: {MaskPixelId(input.PixelId)}", retryCount);
                }
                retryCount++;
                await Task.Delay(BaseRetryDelayMs * (1 << Math.Min(attempt, 8)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return MetaForwardingResult.Failed(MetaForwardingFailureReason.Other,
                    $"Meta forwarding failed: {ex.GetType().Name}. PixelId: {MaskPixelId(input.PixelId)}", retryCount);
            }
        }

        return MetaForwardingResult.Failed(MetaForwardingFailureReason.Other,
            $"Meta forwarding exhausted retries. PixelId: {MaskPixelId(input.PixelId)}", retryCount);
    }

    /// <summary>
    /// Reads the Meta CAPI response body and logs a semantic summary (GA4 shadow validation pattern).
    /// Success body: <c>{"events_received":N,"messages":[…],"fbtrace_id":"…"}</c>; error body:
    /// <c>{"error":{"message","type","code","error_subcode","fbtrace_id"}}</c>. Even with HTTP 200,
    /// Warning if <c>events_received</c> is less than the number of events sent — Meta can silently
    /// drop some events (invalid user_data, old event_time). If the body cannot be parsed, a raw
    /// snippet is logged and it NEVER throws — it does not affect the forwarding result. access_token
    /// is not in the response and the request body is never logged. Returns <c>error.code</c> from
    /// the error body (null if absent) — for rate-limit classification.
    /// </summary>
    private async Task<string?> LogResponseSummaryAsync(HttpResponseMessage response, MetaForwardingInput input, CancellationToken ct)
    {
        var masked = MaskPixelId(input.PixelId);
        var status = (int)response.StatusCode;
        string? bodyText = null;
        try
        {
            bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Meta CAPI response body unreadable (non-fatal). PixelId={PixelId} Status={Status}",
                masked, status);
            return null;
        }

        if (string.IsNullOrWhiteSpace(bodyText))
        {
            _logger.LogInformation("Meta CAPI response body empty. PixelId={PixelId} Status={Status}", masked, status);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var errorCode = ReadNumberOrString(error, "code");
                _logger.LogWarning(
                    "Meta CAPI error response. PixelId={PixelId} Status={Status} Type={ErrorType} Code={ErrorCode} Subcode={ErrorSubcode} FbTraceId={FbTraceId} Message={ErrorMessage}",
                    masked, status,
                    ReadString(error, "type") ?? "?",
                    errorCode ?? "?",
                    ReadNumberOrString(error, "error_subcode") ?? "?",
                    ReadString(error, "fbtrace_id") ?? "?",
                    TruncateForLog(ReadString(error, "message"), 500));
                return errorCode;
            }

            var sent = CountSentEvents(input.RequestBodyJson);
            int? received = root.ValueKind == JsonValueKind.Object
                            && root.TryGetProperty("events_received", out var er)
                            && er.ValueKind == JsonValueKind.Number
                            && er.TryGetInt32(out var receivedValue)
                ? receivedValue
                : null;
            var fbTraceId = root.ValueKind == JsonValueKind.Object ? ReadString(root, "fbtrace_id") : null;

            var messages = new List<string>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var msgs)
                && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    var text = m.ValueKind == JsonValueKind.String ? m.GetString() : m.GetRawText();
                    if (!string.IsNullOrWhiteSpace(text)) messages.Add(TruncateForLog(text, 300));
                }
            }

            if (received is not null && sent > 0 && received.Value < sent)
            {
                _logger.LogWarning(
                    "Meta CAPI partial acceptance: EventsReceived={EventsReceived} < EventsSent={EventsSent}. PixelId={PixelId} Status={Status} FbTraceId={FbTraceId} Messages={Messages}",
                    received.Value, sent, masked, status, fbTraceId ?? "?", string.Join(" | ", messages));
                return null;
            }

            // Full acceptance + no messages = normal flow → Debug (per-event Information inflates log
            // volume for page_view-heavy tenants). Anomalies (messages present, received
            // unreadable) stay at Information so they are visible during live diagnostics.
            var isClean = received is not null && messages.Count == 0 && (sent == 0 || received.Value >= sent);
            _logger.Log(
                isClean ? LogLevel.Debug : LogLevel.Information,
                "Meta CAPI response: EventsReceived={EventsReceived} EventsSent={EventsSent} PixelId={PixelId} Status={Status} FbTraceId={FbTraceId} MessageCount={MessageCount}{Messages}",
                received?.ToString() ?? "?", sent, masked, status, fbTraceId ?? "?", messages.Count,
                messages.Count == 0 ? string.Empty : " Messages=" + string.Join(" | ", messages));
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogInformation(
                "Meta CAPI response body parse failed (non-fatal). PixelId={PixelId} Status={Status} Error={Error} Snippet={Snippet}",
                masked, status, ex.Message, TruncateForLog(bodyText, 300));
            return null;
        }
    }

    /// <summary>Length of <c>data[]</c> in the request body; 0 if it cannot be parsed (comparison is skipped). The body is not logged.</summary>
    private static int CountSentEvents(string requestBodyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBodyJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ReadNumberOrString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null
        };
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "…[truncated]";
    }

    private static (MetaForwardingFailureReason Reason, bool IsRetryable) Classify(HttpStatusCode status)
    {
        var c = (int)status;
        if (c >= 500) return (MetaForwardingFailureReason.ServerError5xx, true);
        // 429 is not retried immediately here (that would worsen the limit); MetaForwardStage hands
        // the event to ForwardingRetryJob for a delayed retry.
        if (c == 429) return (MetaForwardingFailureReason.RateLimited, false);
        if (c >= 400) return (MetaForwardingFailureReason.InvalidRequest4xx, false);
        return (MetaForwardingFailureReason.Other, false);
    }

    /// <summary>
    /// Meta's rate-limit error codes (Graph API rate limiting: 4 application, 17 user,
    /// 32 page, 613 custom limit, 80000-80014 business use case limits). Meta mostly returns these
    /// with HTTP 400, so the status code alone is not enough.
    /// </summary>
    internal static bool IsMetaRateLimitCode(string? code) =>
        int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
        && (c is 4 or 17 or 32 or 613 || c is >= 80000 and <= 80014);

    private static string MaskPixelId(string pixelId)
    {
        if (string.IsNullOrEmpty(pixelId) || pixelId.Length <= 6) return "***";
        return $"{pixelId[..3]}…{pixelId[^3..]}";
    }
}
