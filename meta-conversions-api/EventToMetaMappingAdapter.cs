using System.Text.Json;
using Stobio.Domain.Events;

namespace Stobio.Application.MetaForwarding;

/// <summary>
/// Property-scoped Meta CAPI mapping adapter. Resolves the credential pair, maps the canonical
/// event name to Meta's standard catalog, and produces a forwarding payload conforming to
/// <c>POST graph.facebook.com/v25.0/{pixel}/events</c>.
/// </summary>
public sealed class EventToMetaMappingAdapter : IEventToMetaMappingAdapter
{
    private static readonly JsonSerializerOptions BodyJson = new() { WriteIndented = false };

    private static readonly IReadOnlyDictionary<string, string> EventNameMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["purchase"] = "Purchase",
            ["add_to_cart"] = "AddToCart",
            ["view_content"] = "ViewContent",
            ["product_viewed"] = "ViewContent",
            ["begin_checkout"] = "InitiateCheckout",
            ["checkout_started"] = "InitiateCheckout",
            ["page_view"] = "PageView"
        };

    private readonly IMetaConfigReaderService _configReader;

    public EventToMetaMappingAdapter(IMetaConfigReaderService configReader)
    {
        _configReader = configReader ?? throw new ArgumentNullException(nameof(configReader));
    }

    public async Task<EventToMetaAdaptationResult> AdaptAsync(
        CanonicalEvent canonicalEvent,
        Guid? propertyId,
        Guid rawEventId,
        Guid? clientEventId,
        CancellationToken ct = default)
    {
        if (canonicalEvent is null)
            return EventToMetaAdaptationResult.Failed(["CanonicalEvent is null."]);
        if (canonicalEvent.TenantId == Guid.Empty)
            return EventToMetaAdaptationResult.Failed(["TenantId is empty."]);

        if (propertyId is null || propertyId == Guid.Empty)
            return EventToMetaAdaptationResult.Skipped("PropertyId missing — Meta forwarding is store-scoped.");

        // The tenant identity comes from the event's own row (RawEvent.TenantId → CanonicalEvent.TenantId);
        // another tenant's property → NotFound → Skipped (MetaLastError is not written).
        var configResult = await _configReader.GetConfigAsync(canonicalEvent.TenantId, propertyId.Value, ct);

        // Invalid = credentials exist but are unusable (access token could not be decrypted,
        // PixelId length invalid). This is a delivery failure: return Failed so MetaForwardStage
        // writes MetaLastError and the delivery health card counts it as "Failed". NotFound
        // (not configured) remains Skipped as a deliberate skip.
        if (configResult.Status == MetaConfigReadStatus.Invalid)
            return EventToMetaAdaptationResult.Failed(
                [configResult.Reason ?? "Meta config is invalid or incomplete."],
                configResult.ErrorCode ?? DeliveryErrorCode.InternalError);

        if (configResult.Status != MetaConfigReadStatus.Success || configResult.Config is null)
        {
            return EventToMetaAdaptationResult.Skipped(configResult.Reason ?? configResult.Status.ToString());
        }

        var metaEventName = MapEventName(canonicalEvent.Name);
        var eventTimeUnix = ToUnixSeconds(canonicalEvent.OccurredAtUtc);
        // event_id: client-supplied dedup key wins; otherwise RawEvent.Id is the canonical fallback.
        // Format "D" (hyphenated, lowercase) — must be EXACTLY the same string as the loader's crypto.randomUUID()
        // output and the value in the loader's fbq('track', …, {eventID}) call; Meta pixel↔CAPI
        // dedup checks byte equality (the hyphen-less "N" form did not match).
        var eventId = (clientEventId ?? rawEventId).ToString("D");

        var userData = BuildUserData(canonicalEvent);
        var customData = BuildCustomData(canonicalEvent.Properties);

        var dataItem = new Dictionary<string, object?>
        {
            ["event_name"] = metaEventName,
            ["event_time"] = eventTimeUnix,
            ["event_id"] = eventId,
            ["action_source"] = "website",
            ["user_data"] = userData
        };
        if (customData.Count > 0)
            dataItem["custom_data"] = customData;
        if (!string.IsNullOrWhiteSpace(canonicalEvent.PageUrl))
            dataItem["event_source_url"] = canonicalEvent.PageUrl;

        var body = new Dictionary<string, object?>
        {
            ["data"] = new[] { dataItem },
            ["access_token"] = configResult.Config.AccessToken
        };

        // Test mode — top-level field so events appear instantly in the Events Manager → Test Events
        // panel. For pilot/validation; if null, normal flow (included in reports).
        if (!string.IsNullOrWhiteSpace(configResult.Config.TestEventCode))
        {
            body["test_event_code"] = configResult.Config.TestEventCode;
        }

        var bodyJson = JsonSerializer.Serialize(body, BodyJson);

        return EventToMetaAdaptationResult.Success(new MetaForwardingInput
        {
            PixelId = configResult.Config.PixelId,
            AccessToken = configResult.Config.AccessToken,
            RequestBodyJson = bodyJson
        });
    }

    private static string MapEventName(string canonicalName)
    {
        if (EventNameMap.TryGetValue(canonicalName, out var mapped))
            return mapped;
        // Fallback: title-case the snake_case canonical name.
        var parts = canonicalName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static long ToUnixSeconds(DateTime utc)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(u).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Builds Meta CAPI <c>user_data</c> from the canonical client context. Single source
    /// of truth: <see cref="CanonicalEvent.ClientContext"/>. No raw payload reads —
    /// the loader, EventIngestionService and EventMappingEngine chains carry client metadata
    /// onto the canonical event.
    /// </summary>
    private static Dictionary<string, object?> BuildUserData(CanonicalEvent canonicalEvent)
    {
        var ud = new Dictionary<string, object?>();
        var cc = canonicalEvent.ClientContext;

        if (cc is not null)
        {
            if (!string.IsNullOrWhiteSpace(cc.IpAddress)) ud["client_ip_address"] = cc.IpAddress;
            if (!string.IsNullOrWhiteSpace(cc.UserAgent)) ud["client_user_agent"] = cc.UserAgent;
            if (!string.IsNullOrWhiteSpace(cc.Fbp)) ud["fbp"] = cc.Fbp;
            if (!string.IsNullOrWhiteSpace(cc.Fbc)) ud["fbc"] = cc.Fbc;
            if (!string.IsNullOrWhiteSpace(cc.Language)) ud["locale"] = cc.Language;

            // Hashed identifiers. Meta em/ph expects SHA-256 lowercase hex
            // — PiiHasher output matches exactly, so it is not re-hashed. Raw PII never reaches
            // this point (redacted at ingest).
            if (!string.IsNullOrWhiteSpace(cc.EmailHash)) ud["em"] = cc.EmailHash;
            if (!string.IsNullOrWhiteSpace(cc.PhoneHash)) ud["ph"] = cc.PhoneHash;
        }

        // external_id: if a hashed external id (tenant CRM/order customer ID) exists, it wins;
        // otherwise the existing AnonymousId → SessionId fallback is kept AS IS (no regression).
        var externalId = !string.IsNullOrWhiteSpace(cc?.ExternalIdHash)
            ? cc!.ExternalIdHash
            : canonicalEvent.AnonymousId ?? canonicalEvent.SessionId;
        if (!string.IsNullOrWhiteSpace(externalId))
            ud["external_id"] = externalId;

        return ud;
    }

    private static Dictionary<string, object?> BuildCustomData(IReadOnlyDictionary<string, object?> props)
    {
        var cd = new Dictionary<string, object?>();
        if (props is null || props.Count == 0)
            return cd;

        // Pass through commerce-relevant fields when present.
        foreach (var key in new[] { "value", "currency", "content_ids", "content_name", "content_type", "num_items", "order_id" })
        {
            if (props.TryGetValue(key, out var v) && v is not null)
                cd[key] = v;
        }

        // The GA4 e-commerce schema carries the order number as transaction_id (Shopify pixel and
        // dataLayer purchase); Meta's equivalent is order_id. If order_id already exists, it wins.
        if (!cd.ContainsKey("order_id") && props.TryGetValue("transaction_id", out var txn) && txn is not null)
            cd["order_id"] = txn;

        return cd;
    }

}
