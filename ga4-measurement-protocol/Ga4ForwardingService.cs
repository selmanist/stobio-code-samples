using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stobio.Application.Ga4Forwarding;
using Stobio.Shared.Configuration;

namespace Stobio.Infrastructure.Ga4Forwarding;

/// <summary>
/// Forwards events to GA4 Measurement Protocol with retry and error handling.
/// Classifies errors as retryable (timeout, 5xx, connection) vs non-retryable (4xx, invalid config).
/// Uses exponential backoff for retries. Never logs secrets or full request body.
/// </summary>
public sealed class Ga4ForwardingService : IGa4ForwardingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Ga4Settings _settings;
    private readonly ILogger<Ga4ForwardingService> _logger;

    public Ga4ForwardingService(
        IHttpClientFactory httpClientFactory,
        IOptions<Ga4Settings> settings,
        ILogger<Ga4ForwardingService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Ga4ForwardingResult> ForwardAsync(Ga4ForwardingInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var endpoint = string.IsNullOrEmpty(_settings.MeasurementProtocolEndpoint)
            ? Ga4Settings.DefaultMeasurementProtocolEndpoint
            : _settings.MeasurementProtocolEndpoint;

        var url = $"{endpoint.TrimEnd('/')}?measurement_id={Uri.EscapeDataString(input.MeasurementId)}&api_secret={Uri.EscapeDataString(input.ApiSecret)}";

        // DebugView routing experiment: according to some community reports, in GA4 MP v2 server-side
        // the event-level `debug_mode:1` param alone does not trigger routing to DebugView;
        // it may also require `&debug_mode=1` in the URL query string. Low-risk addition,
        // does not break production reporting behavior (Google ignores unknown query params).
        if (input.DebugMode)
        {
            url += "&debug_mode=1";
        }

        var result = await ForwardCoreAsync(input, url, ct).ConfigureAwait(false);

        // Pilot diagnostics: while DebugMode is active, also send the same payload to the GA4 validation endpoint
        // and write the returned validationMessages to the Information log. The production dispatch result is unaffected —
        // the shadow is fired in every case (success + fail), with exceptions swallowed.
        if (input.DebugMode && _settings.DebugValidationShadowEnabled)
        {
            await SendDebugValidationShadowAsync(input, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<Ga4ForwardingResult> ForwardCoreAsync(Ga4ForwardingInput input, string url, CancellationToken ct)
    {
        var maxRetries = Math.Max(0, _settings.MaxRetryCount);
        var retryCount = 0;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient("Ga4MeasurementProtocol");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.HttpTimeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(input.RequestBodyJson, Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    if (retryCount > 0)
                    {
                        _logger.LogDebug(
                            "GA4 forwarding succeeded after {RetryCount} retries. MeasurementId: {MeasurementId}",
                            retryCount, MaskMeasurementId(input.MeasurementId));
                    }
                    return Ga4ForwardingResult.Success(retryCount);
                }

                var (failureReason, isRetryable) = ClassifyHttpStatus(response.StatusCode);

                if (!isRetryable || attempt >= maxRetries)
                {
                    if (failureReason == Ga4ForwardingFailureReason.RateLimited)
                    {
                        // Retry-After is only logged; the wait is ForwardingRetryJob's existing
                        // backoff. Whether the header actually arrives is tracked from this log.
                        _logger.LogWarning(
                            "GA4 rate limited. MeasurementId: {MeasurementId} RetryAfter: {RetryAfter}",
                            MaskMeasurementId(input.MeasurementId), response.Headers.RetryAfter?.ToString() ?? "none");
                    }
                    var msg = $"GA4 returned {(int)response.StatusCode} ({response.StatusCode}). MeasurementId: {MaskMeasurementId(input.MeasurementId)}";
                    return Ga4ForwardingResult.Failed(failureReason, msg, retryCount);
                }

                retryCount++;
                await ApplyBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken != ct)
            {
                var (failureReason, isRetryable) = (Ga4ForwardingFailureReason.NetworkTimeout, true);
                if (attempt >= maxRetries)
                {
                    // The retry pipeline (ForwardingRetryJob) already absorbs this transient timeout.
                    // Warning severity is visible in logs but is not pushed to Sentry (min Error).
                    _logger.LogWarning(
                        "GA4 request timed out after {RetryCount} retries. MeasurementId: {MeasurementId}",
                        retryCount, MaskMeasurementId(input.MeasurementId));
                    return Ga4ForwardingResult.Failed(
                        failureReason,
                        "GA4 request timed out. MeasurementId: " + MaskMeasurementId(input.MeasurementId),
                        retryCount);
                }
                retryCount++;
                _logger.LogDebug(
                    "GA4 request timeout, retry {Attempt}/{MaxRetries}. MeasurementId: {MeasurementId}",
                    attempt + 1, maxRetries, MaskMeasurementId(input.MeasurementId));
                await ApplyBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var (failureReason, isRetryable) = (Ga4ForwardingFailureReason.ConnectionError, true);
                if (attempt >= maxRetries)
                {
                    return Ga4ForwardingResult.Failed(
                        failureReason,
                        $"GA4 request failed: {ex.Message}. MeasurementId: {MaskMeasurementId(input.MeasurementId)}",
                        retryCount);
                }
                retryCount++;
                _logger.LogDebug(ex,
                    "GA4 request connection error, retry {Attempt}/{MaxRetries}. MeasurementId: {MeasurementId}",
                    attempt + 1, maxRetries, MaskMeasurementId(input.MeasurementId));
                await ApplyBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var (failureReason, _) = ClassifyException(ex);
                return Ga4ForwardingResult.Failed(
                    failureReason,
                    $"GA4 forwarding failed: {ex.GetType().Name}. MeasurementId: {MaskMeasurementId(input.MeasurementId)}",
                    retryCount);
            }
        }

        return Ga4ForwardingResult.Failed(
            Ga4ForwardingFailureReason.Other,
            "GA4 forwarding exhausted retries. MeasurementId: " + MaskMeasurementId(input.MeasurementId),
            retryCount);
    }

    private static (Ga4ForwardingFailureReason Reason, bool IsRetryable) ClassifyHttpStatus(HttpStatusCode status)
    {
        var code = (int)status;
        if (code >= 500)
            return (Ga4ForwardingFailureReason.ServerError5xx, true);
        // 429 is not retried immediately here (that would worsen the limit); ForwardStage hands
        // the event to ForwardingRetryJob for a delayed retry.
        if (code == 429)
            return (Ga4ForwardingFailureReason.RateLimited, false);
        if (code >= 400)
            return (Ga4ForwardingFailureReason.InvalidRequest4xx, false);
        return (Ga4ForwardingFailureReason.Other, false);
    }

    private static (Ga4ForwardingFailureReason Reason, bool IsRetryable) ClassifyException(Exception ex)
    {
        if (ex is HttpRequestException)
            return (Ga4ForwardingFailureReason.ConnectionError, true);
        if (ex is TaskCanceledException)
            return (Ga4ForwardingFailureReason.NetworkTimeout, true);
        if (ex is TimeoutException)
            return (Ga4ForwardingFailureReason.NetworkTimeout, true);
        if (ex is ArgumentException or FormatException)
            return (Ga4ForwardingFailureReason.InvalidConfig, false);
        return (Ga4ForwardingFailureReason.Other, false);
    }

    private async Task ApplyBackoffAsync(int attempt, CancellationToken ct)
    {
        var delayMs = _settings.BaseRetryDelayMs * (1 << Math.Min(attempt, 10));
        await Task.Delay(delayMs, ct).ConfigureAwait(false);
    }

    private async Task SendDebugValidationShadowAsync(Ga4ForwardingInput input, CancellationToken ct)
    {
        try
        {
            var endpoint = string.IsNullOrEmpty(_settings.DebugValidationEndpoint)
                ? Ga4Settings.DefaultDebugValidationEndpoint
                : _settings.DebugValidationEndpoint;

            var url = $"{endpoint.TrimEnd('/')}?measurement_id={Uri.EscapeDataString(input.MeasurementId)}&api_secret={Uri.EscapeDataString(input.ApiSecret)}";

            using var client = _httpClientFactory.CreateClient("Ga4MeasurementProtocol");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.HttpTimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(input.RequestBodyJson, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // The hosting provider's log viewer truncates lines at row width in list view. Instead of the raw body
            // we parse the payload and write a semantic summary — each validationMessage on its own
            // log line, fully visible in the dashboard with a short format. On parse failure, fall back to the raw body.
            _logger.LogInformation(
                "GA4 debug validation shadow: MeasurementId={MeasurementId} Status={Status} BodyLength={BodyLength}",
                MaskMeasurementId(input.MeasurementId),
                (int)response.StatusCode,
                bodyText?.Length ?? 0);

            LogTimestampLag(input.MeasurementId, input.RequestBodyJson);
            LogValidationSummary(input.MeasurementId, bodyText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled; do not log noise.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "GA4 debug validation shadow failed (non-fatal). MeasurementId={MeasurementId}",
                MaskMeasurementId(input.MeasurementId));
        }
    }

    private void LogTimestampLag(string measurementId, string requestBodyJson)
    {
        // GA4 DebugView has a "Last 30 minutes" filter. If timestamp_micros is older than 1800s,
        // the event does not show up in DebugView even with a valid payload — it lands in Reports. We write the lag
        // to the Information log so the stale-timestamp hypothesis can be confirmed/ruled out immediately during live diagnostics.
        try
        {
            using var doc = JsonDocument.Parse(requestBodyJson);
            if (!doc.RootElement.TryGetProperty("timestamp_micros", out var tsEl)) return;
            if (!tsEl.TryGetInt64(out var tsMicros)) return;

            var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(tsMicros / 1000);
            var lagSeconds = (DateTimeOffset.UtcNow - eventTime).TotalSeconds;
            var withinDebugWindow = lagSeconds <= 1800;

            _logger.LogInformation(
                "GA4 shadow timestamp: MeasurementId={MeasurementId} EventTimeUtc={EventTime:o} LagSeconds={LagSeconds:F1} WithinDebugView30mWindow={WithinWindow}",
                MaskMeasurementId(measurementId), eventTime.UtcDateTime, lagSeconds, withinDebugWindow);
        }
        catch (JsonException)
        {
            // Swallow silently — diagnostic log; a failure does not affect the production flow.
        }
    }

    private void LogValidationSummary(string measurementId, string? bodyText)
    {
        var masked = MaskMeasurementId(measurementId);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            _logger.LogInformation("GA4 shadow body empty: MeasurementId={MeasurementId}", masked);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (!doc.RootElement.TryGetProperty("validationMessages", out var msgs)
                || msgs.ValueKind != JsonValueKind.Array)
            {
                _logger.LogInformation(
                    "GA4 shadow body parse: validationMessages field missing. MeasurementId={MeasurementId} BodySnippet={Snippet}",
                    masked, TruncateForLog(bodyText, 300));
                return;
            }

            var count = msgs.GetArrayLength();
            if (count == 0)
            {
                _logger.LogInformation(
                    "GA4 shadow OK: empty validationMessages (payload valid). MeasurementId={MeasurementId}",
                    masked);
                return;
            }

            _logger.LogInformation(
                "GA4 shadow PROBLEM: {Count} validation message(s). MeasurementId={MeasurementId}",
                count, masked);

            var index = 0;
            foreach (var msg in msgs.EnumerateArray())
            {
                var description = msg.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                var code = msg.TryGetProperty("validationCode", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                var fieldPath = msg.TryGetProperty("fieldPath", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString()
                    : null;

                _logger.LogInformation(
                    "GA4 shadow MSG[{Index}]: code={Code} field={FieldPath} desc={Description}",
                    index,
                    code ?? "?",
                    fieldPath ?? "?",
                    TruncateForLog(description, 500) ?? "?");
                index++;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogInformation(
                "GA4 shadow body parse failed: MeasurementId={MeasurementId} Error={Error} Snippet={Snippet}",
                masked, ex.Message, TruncateForLog(bodyText, 300));
        }
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "…[truncated]";
    }

    /// <summary>
    /// Masks MeasurementId for logs: shows first 4 and last 2 chars (e.g. G-XX...XX).
    /// </summary>
    private static string MaskMeasurementId(string measurementId)
    {
        if (string.IsNullOrEmpty(measurementId) || measurementId.Length <= 8)
            return "***";
        return $"{measurementId[..4]}...{measurementId[^2..]}";
    }
}
