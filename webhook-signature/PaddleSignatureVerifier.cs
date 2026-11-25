using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stobio.Infrastructure.Paddle;

/// <summary>
/// Paddle Billing webhook signature verifier.
///
/// <para>
/// Paddle sends a value in the <c>Paddle-Signature</c> header in the format
/// <c>ts=&lt;unix&gt;;h1=&lt;hex-hmac&gt;</c>. Verification:
/// </para>
/// <list type="number">
///   <item>Parse the header and extract the <c>ts</c> and <c>h1</c> values.</item>
///   <item>Compute HMAC-SHA256(secret, <c>"{ts}:{rawBody}"</c>).</item>
///   <item>Compare the computed value with <c>h1</c> in <strong>constant time</strong>
///         (<see cref="CryptographicOperations.FixedTimeEquals"/>) — string equality
///         opens a timing attack, which we avoid.</item>
///   <item>(Optional) Verify that <c>ts</c> is within the tolerance window — replay protection.</item>
/// </list>
///
/// <para>
/// <strong>IsConfigured guard:</strong> when <see cref="PaddleSettings.IsConfigured"/> is false,
/// no verification is done; the endpoint layer returns 503 and does not start a real HTTP call.
/// </para>
/// </summary>
public sealed class PaddleSignatureVerifier
{
    /// <summary>
    /// Signature header name of a Paddle delivery.
    /// </summary>
    public const string SignatureHeaderName = "Paddle-Signature";

    /// <summary>
    /// Replay window — Paddle recommends 5 minutes. Timestamps outside this window
    /// are rejected (tolerance for delivery delay + clock skew).
    /// </summary>
    private static readonly TimeSpan ReplayTolerance = TimeSpan.FromMinutes(5);

    private readonly IOptions<PaddleSettings> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaddleSignatureVerifier> _logger;

    public PaddleSignatureVerifier(
        IOptions<PaddleSettings> options,
        TimeProvider timeProvider,
        ILogger<PaddleSignatureVerifier> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Is the webhook secret configured? The endpoint returns 503 when this is false.
    /// <para>
    /// A secret-only check, which is sufficient for the webhook side;
    /// <see cref="PaddleSettings.IsConfigured"/> requires ApiKey + WebhookSecret together
    /// (the checkout flow does not work without a server-side API key). This narrower surface
    /// keeps webhook intake from falling back to 503 even when the ApiKey is not yet available during
    /// vendor onboarding (Paddle deliveries are verified and deduplicated).
    /// </para>
    /// </summary>
    public bool IsWebhookSecretConfigured => !string.IsNullOrWhiteSpace(_options.Value.WebhookSecret);

    /// <summary>
    /// Verify the signature. Returns <c>false</c> if the header is missing/malformed, the timestamp is stale, or the HMAC does not match.
    /// </summary>
    /// <param name="rawBody">Raw body string of the webhook request — must be read <strong>before parsing</strong>.
    /// If re-serialized, the signature will not match (whitespace/property ordering changes).</param>
    /// <param name="signatureHeader"><c>Paddle-Signature</c> header value.</param>
    public bool Verify(string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            _logger.LogWarning("stobio.paddle.webhook.signature_missing reason=header_absent");
            return false;
        }

        var secret = _options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // The IsConfigured guard normally short-circuits at the endpoint; checked here too, defensively.
            _logger.LogWarning("stobio.paddle.webhook.signature_missing reason=secret_unconfigured");
            return false;
        }

        if (!TryParseHeader(signatureHeader, out var ts, out var providedHmacHex))
        {
            _logger.LogWarning("stobio.paddle.webhook.signature_invalid reason=header_malformed");
            return false;
        }

        // Replay window: Paddle ts is unix seconds. Reject deliveries outside now ± 5 min.
        // ts may be in the future but within tolerance (clock skew) — hence abs(diff).
        var nowUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var diff = Math.Abs(nowUnix - ts);
        if (diff > (long)ReplayTolerance.TotalSeconds)
        {
            _logger.LogWarning(
                "stobio.paddle.webhook.signature_invalid reason=timestamp_outside_window ts={Ts} now={Now} diffSeconds={Diff}",
                ts,
                nowUnix,
                diff);
            return false;
        }

        // HMAC-SHA256(secret, "{ts}:{rawBody}") — consistent with Paddle's verification documentation.
        var signedPayload = $"{ts.ToString(CultureInfo.InvariantCulture)}:{rawBody}";
        byte[] computedHash;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
        {
            computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        }

        byte[] providedHash;
        try
        {
            providedHash = Convert.FromHexString(providedHmacHex);
        }
        catch (FormatException)
        {
            _logger.LogWarning("stobio.paddle.webhook.signature_invalid reason=hmac_not_hex");
            return false;
        }

        if (providedHash.Length != computedHash.Length)
        {
            // The length check is not timing-safe, but a hash of a different length is invalid anyway —
            // FixedTimeEquals needs both sides to be the same length, hence the early exit here.
            _logger.LogWarning("stobio.paddle.webhook.signature_invalid reason=hmac_length_mismatch");
            return false;
        }

        var match = CryptographicOperations.FixedTimeEquals(computedHash, providedHash);
        if (!match)
        {
            _logger.LogWarning("stobio.paddle.webhook.signature_invalid reason=hmac_mismatch");
        }

        return match;
    }

    /// <summary>
    /// Parses the <c>ts=1700000000;h1=abcdef…</c> format. Multiple h-versions (h1, h2, …)
    /// may arrive, but Paddle Billing v1 only produces h1; if that changes, "pick the strongest
    /// one" logic can be added. For now, false if h1 is missing.
    /// </summary>
    private static bool TryParseHeader(string header, out long ts, out string hmacHex)
    {
        ts = 0;
        hmacHex = string.Empty;

        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long? parsedTs = null;
        string? parsedHmac = null;

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
            {
                continue;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();

            if (key.Equals("ts", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    parsedTs = v;
                }
            }
            else if (key.Equals("h1", StringComparison.OrdinalIgnoreCase))
            {
                parsedHmac = value;
            }
        }

        if (parsedTs is null || string.IsNullOrWhiteSpace(parsedHmac))
        {
            return false;
        }

        ts = parsedTs.Value;
        hmacHex = parsedHmac!;
        return true;
    }
}
