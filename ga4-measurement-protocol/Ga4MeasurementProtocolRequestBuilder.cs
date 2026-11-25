using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stobio.Domain.Events;

namespace Stobio.Application.Ga4Forwarding;

/// <summary>
/// Builds GA4 Measurement Protocol request body from canonical events.
/// Single canonical event → single GA4 event. No HTTP dispatch.
/// client_id strategy: AnonymousId ?? SessionId; if both missing → event drop (fail with validation error).
/// </summary>
public sealed class Ga4MeasurementProtocolRequestBuilder : IGa4MeasurementProtocolRequestBuilder
{
    private const int Ga4MaxEventNameLength = 40;
    private static readonly string[] Ga4ReservedParamPrefixes = { "_", "ga_", "google_", "firebase_", "gtag." };
    private const int Ga4MaxItems = 200;

    // Top-level parameters of GA4 recommended e-commerce events (lifted from the GTM `ecommerce` wrapper).
    private static readonly HashSet<string> EcommerceLiftKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "currency", "value", "transaction_id", "coupon", "shipping", "tax", "items"
    };

    // GA4 item parameters (developers.google.com/analytics — recommended events, items[]).
    private static readonly HashSet<string> Ga4ItemParamKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "item_id", "item_name", "affiliation", "coupon", "discount", "index", "item_brand",
        "item_category", "item_category2", "item_category3", "item_category4", "item_category5",
        "item_list_id", "item_list_name", "item_variant", "location_id", "price", "quantity"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <inheritdoc />
    public Ga4BuildResult Build(CanonicalEvent canonicalEvent, bool debugMode = false)
    {
        if (canonicalEvent == null)
            return Ga4BuildResult.Fail(["CanonicalEvent is null."]);

        var errors = new List<string>();
        var warnings = new List<string>();

        // client_id: AnonymousId ?? SessionId. GA4 requires client_id for web. Both missing → drop.
        var clientId = ResolveClientId(canonicalEvent);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            errors.Add("client_id required; AnonymousId and SessionId are both missing. Event cannot be forwarded to GA4.");
            return Ga4BuildResult.Fail(errors, DeliveryErrorCode.MissingClientId);
        }

        // Event name: the canonical name is converted to the GA4 recommended name (product_viewed → view_item;
        // the table lives in Ga4EventNameMap). GA4 max 40 chars, snake_case.
        var eventName = SanitizeEventName(Ga4EventNameMap.ToGa4((canonicalEvent.Name ?? string.Empty).Trim()), warnings);
        if (string.IsNullOrEmpty(eventName))
        {
            errors.Add("Event name is empty after sanitization.");
            return Ga4BuildResult.Fail(errors, DeliveryErrorCode.InvalidEventName);
        }

        // params: filter null/empty, filter reserved prefixes
        var ga4Params = BuildGa4Params(canonicalEvent.Properties, warnings);

        // Canonical client context → GA4 Measurement Protocol event params.
        // Officially recognized as standard event params in GA4 MP v2:
        // page_location, page_referrer, language, gclid, viewport_size, screen_resolution.
        // ip_override + user_agent are only meaningful in an sGTM (server-side GTM) relay;
        // the plain MP endpoint does not process these overrides, so to avoid a false promise
        // these two fields were deliberately removed from the adapter (sGTM is a separate epic).
        // Null-safe — missing fields are skipped. If the customer provides the same key
        // in the event properties, the customer's value takes precedence (TryAdd pattern).
        AddClientContextParams(ga4Params, canonicalEvent.ClientContext);

        // Per the GA4 MP docs, every event's params must include engagement_time_msec and
        // session_id for active user/session and Realtime counting. A customer-supplied value is kept.
        AddEngagementParams(ga4Params, canonicalEvent.SessionId);

        // debug_mode is reserved-prefix-free; safe to set as a regular event param. GA4 sees
        // any truthy value and routes the event to DebugView. Use 1 (int) per docs.
        if (debugMode)
        {
            ga4Params["debug_mode"] = 1;
        }

        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timestampMicros = (long)((canonicalEvent.OccurredAtUtc - unixEpoch).TotalMilliseconds * 1000);

        var payload = new Ga4Payload
        {
            ClientId = clientId.Trim(),
            UserId = string.IsNullOrWhiteSpace(canonicalEvent.UserId) ? null : canonicalEvent.UserId.Trim(),
            UserData = BuildUserData(canonicalEvent.ClientContext),
            TimestampMicros = timestampMicros,
            Events = new[]
            {
                new Ga4Event
                {
                    Name = eventName,
                    Params = ga4Params.Count > 0 ? ga4Params : null
                }
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            return Ga4BuildResult.Ok(json, warnings.Count > 0 ? warnings : null);
        }
        catch (JsonException ex)
        {
            errors.Add($"Payload serialization failed: {ex.Message}");
            return Ga4BuildResult.Fail(errors);
        }
    }

    private static string? ResolveClientId(CanonicalEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.AnonymousId))
            return e.AnonymousId;
        if (!string.IsNullOrWhiteSpace(e.SessionId))
            return e.SessionId;
        return null;
    }

    private static string SanitizeEventName(string name, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var s = name.Trim();
        if (s.Length > Ga4MaxEventNameLength)
        {
            s = s[..Ga4MaxEventNameLength];
            warnings.Add($"Event name truncated to {Ga4MaxEventNameLength} chars for GA4 compatibility.");
        }

        return s;
    }

    private static Dictionary<string, object?> BuildGa4Params(
        IReadOnlyDictionary<string, object?> properties,
        List<string> warnings)
    {
        if (properties == null || properties.Count == 0)
            return new Dictionary<string, object?>();

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var filtered = 0;
        object? ecommerce = null;

        foreach (var kv in properties)
        {
            if (ShouldSkipParam(kv.Key, kv.Value, out var skipReason))
            {
                filtered++;
                continue;
            }

            if (string.Equals(kv.Key, "items", StringComparison.OrdinalIgnoreCase))
            {
                var items = ToGa4Items(kv.Value, warnings);
                if (items != null)
                    result["items"] = items;
                continue;
            }

            if (string.Equals(kv.Key, "ecommerce", StringComparison.OrdinalIgnoreCase))
            {
                ecommerce = kv.Value;
                continue;
            }

            var value = ToGa4ParamValue(kv.Value);
            if (value == null)
                continue;

            result[kv.Key] = value;
        }

        if (ecommerce != null)
            LiftEcommerceParams(result, ecommerce, warnings);

        if (filtered > 0)
            warnings.Add($"{filtered} param(s) filtered (reserved prefix or empty value).");

        return result;
    }

    /// <summary>
    /// A GTM ecommerce push (<c>{ event: 'view_item', ecommerce: { currency, value, items } }</c>)
    /// needs top-level parameters in GA4 MP; only known e-commerce keys are lifted up.
    /// If the same key already exists at the top level, it is kept (TryAdd pattern).
    /// </summary>
    private static void LiftEcommerceParams(
        Dictionary<string, object?> ga4Params,
        object ecommerce,
        List<string> warnings)
    {
        if (ToJsonElement(ecommerce) is not { ValueKind: JsonValueKind.Object } el)
            return;

        foreach (var prop in el.EnumerateObject())
        {
            if (!EcommerceLiftKeys.Contains(prop.Name) || ga4Params.ContainsKey(prop.Name))
                continue;

            var value = string.Equals(prop.Name, "items", StringComparison.OrdinalIgnoreCase)
                ? ToGa4Items(prop.Value, warnings)
                : ToGa4ParamValue(prop.Value);
            if (value is null or "")
                continue;

            ga4Params[prop.Name.ToLowerInvariant()] = value;
        }
    }

    /// <summary>
    /// GA4 <c>items</c> array. Only item parameters documented by GA4 and scalar values
    /// pass through (free-form keys from the customer's dataLayer — possible PII included — do not leave).
    /// Items without item_id or item_name are skipped; at most <see cref="Ga4MaxItems"/> items.
    /// </summary>
    private static List<Dictionary<string, object?>>? ToGa4Items(object? value, List<string> warnings)
    {
        if (ToJsonElement(value) is not { ValueKind: JsonValueKind.Array } arr)
            return null;

        var items = new List<Dictionary<string, object?>>();
        var skipped = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (items.Count >= Ga4MaxItems)
            {
                warnings.Add($"items truncated to {Ga4MaxItems} for GA4 compatibility.");
                break;
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.EnumerateObject())
            {
                if (!Ga4ItemParamKeys.Contains(p.Name))
                    continue;
                var v = ToGa4ParamValue(p.Value);
                if (v is null or "")
                    continue;
                item[p.Name.ToLowerInvariant()] = v;
            }

            if (!item.ContainsKey("item_id") && !item.ContainsKey("item_name"))
            {
                skipped++;
                continue;
            }

            items.Add(item);
        }

        if (skipped > 0)
            warnings.Add($"{skipped} item(s) skipped (not an object or missing item_id/item_name).");

        return items.Count > 0 ? items : null;
    }

    private static JsonElement? ToJsonElement(object? value)
    {
        if (value is null)
            return null;
        if (value is JsonElement je)
            return je;
        try
        {
            return JsonSerializer.SerializeToElement(value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// GA4 Enhanced Conversions (user-provided data) block.
    /// Only a non-empty email hash is added; if empty, the block is not produced at all (null →
    /// not serialized due to WhenWritingNull) — output for an event without identity data
    /// stays bit-for-bit identical to the existing behavior. The hash is lowercase SHA-256 hex
    /// produced at ingest; it is not re-hashed, and raw PII never reaches this point.
    /// PHONE IS DELIBERATELY NOT SENT: the existing PhoneHash is digits-only (Meta ph format),
    /// whereas GA4 expects the hash of an E.164-normalized number — since the formats differ it
    /// would never match, so a worthless field is not sent. E.164 normalization / dual-hash is a separate decision;
    /// until then sha256_phone_number is not sent to GA4 (Meta ph is unaffected).
    /// </summary>
    private static Ga4UserData? BuildUserData(ClientContext? cc)
    {
        if (cc is null)
            return null;

        var emailHash = string.IsNullOrWhiteSpace(cc.EmailHash) ? null : cc.EmailHash.Trim();
        if (emailHash is null)
            return null;

        return new Ga4UserData
        {
            Sha256EmailAddress = new[] { emailHash }
        };
    }

    private static void AddClientContextParams(
        Dictionary<string, object?> ga4Params,
        ClientContext? cc)
    {
        if (cc is null) return;

        // If the customer's event has the same key, the customer's value is kept (TryAdd).
        // Client context only acts as a fallback / fills in missing information.
        void Set(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (ga4Params.ContainsKey(key)) return;
            ga4Params[key] = value.Trim();
        }

        Set("page_location", cc.PageUrl);
        Set("page_referrer", cc.PageReferrer);
        Set("language", cc.Language);
        Set("gclid", cc.Gclid);

        if (cc.ViewportWidth is > 0 && cc.ViewportHeight is > 0
            && !ga4Params.ContainsKey("viewport_size"))
            ga4Params["viewport_size"] = $"{cc.ViewportWidth.Value}x{cc.ViewportHeight.Value}";
        if (cc.ScreenWidth is > 0 && cc.ScreenHeight is > 0
            && !ga4Params.ContainsKey("screen_resolution"))
            ga4Params["screen_resolution"] = $"{cc.ScreenWidth.Value}x{cc.ScreenHeight.Value}";
    }

    /// <summary>
    /// engagement_time_msec: the server side does not know the real engagement time; the smallest
    /// positive value (1 ms) needed for GA4 to count the event as an active user is written — it does
    /// not inflate average engagement time.
    /// session_id: GA4 accepts digits only (<c>^\d+$</c>). The event's own session ID
    /// (the tab session UUID from the loader/Shopify pixel) is reduced via SHA-256 to a 53-bit positive
    /// number: events from the same session get the same session_id. The only source is the event's own
    /// session ID; when AnonymousId is present, the hash of this ID is sent to GA4 for the first time (the raw value
    /// is not sent; not personal data, same class as gtag's ga_session_id). If there is no session ID,
    /// session_id is not written. Session merging with gtag is not expected: client_id is not gtag's either.
    /// </summary>
    private static void AddEngagementParams(Dictionary<string, object?> ga4Params, string? sessionId)
    {
        ga4Params.TryAdd("engagement_time_msec", 1);

        if (string.IsNullOrWhiteSpace(sessionId) || ga4Params.ContainsKey("session_id"))
            return;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Trim()));
        var numeric = (long)(BinaryPrimitives.ReadUInt64BigEndian(hash) & 0x1F_FFFF_FFFF_FFFF);
        ga4Params["session_id"] = numeric == 0 ? 1 : numeric;
    }

    private static bool ShouldSkipParam(string key, object? value, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            reason = "empty key";
            return true;
        }

        if (value == null)
        {
            reason = "null value";
            return true;
        }

        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            reason = "empty string";
            return true;
        }

        var keyLower = key.Trim().ToLowerInvariant();
        foreach (var prefix in Ga4ReservedParamPrefixes)
        {
            if (keyLower.StartsWith(prefix, StringComparison.Ordinal))
            {
                reason = $"reserved prefix '{prefix}'";
                return true;
            }
        }

        return false;
    }

    private static object? ToGa4ParamValue(object? value)
    {
        if (value == null)
            return null;

        if (value is System.Text.Json.JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString()?.Trim(),
                JsonValueKind.Number => je.TryGetInt64(out var i) ? i : je.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null
            };
        }

        if (value is string str)
            return str.Trim();

        if (value is bool or int or long or double)
            return value;

        if (value is decimal d)
            return (double)d;

        return null;
    }

    private sealed class Ga4Payload
    {
        [JsonPropertyName("client_id")]
        public string ClientId { get; init; } = string.Empty;

        [JsonPropertyName("user_id")]
        public string? UserId { get; init; }

        [JsonPropertyName("user_data")]
        public Ga4UserData? UserData { get; init; }

        [JsonPropertyName("timestamp_micros")]
        public long? TimestampMicros { get; init; }

        [JsonPropertyName("events")]
        public Ga4Event[]? Events { get; init; }
    }

    private sealed class Ga4Event
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("params")]
        public Dictionary<string, object?>? Params { get; init; }
    }

    /// <summary>GA4 MP user-provided data (Enhanced Conversions) — fields are string arrays per the official schema.</summary>
    private sealed class Ga4UserData
    {
        [JsonPropertyName("sha256_email_address")]
        public string[]? Sha256EmailAddress { get; init; }

        // sha256_phone_number is deliberately absent — see BuildUserData (E.164 format mismatch).
    }
}
