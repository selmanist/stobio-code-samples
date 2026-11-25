using Stobio.Domain.Events;

namespace Stobio.Application.Ga4Forwarding;

/// <summary>
/// Property-scoped GA4 mapping adapter. Resolves the credential pair for the event's
/// <c>PropertyId</c> and produces a forwarding input via the Measurement Protocol builder.
/// </summary>
public sealed class EventToGa4MappingAdapter : IEventToGa4MappingAdapter
{
    private readonly IGa4ConfigReaderService _configReader;
    private readonly IGa4MeasurementProtocolRequestBuilder _requestBuilder;

    public EventToGa4MappingAdapter(
        IGa4ConfigReaderService configReader,
        IGa4MeasurementProtocolRequestBuilder requestBuilder)
    {
        _configReader = configReader ?? throw new ArgumentNullException(nameof(configReader));
        _requestBuilder = requestBuilder ?? throw new ArgumentNullException(nameof(requestBuilder));
    }

    /// <inheritdoc />
    public async Task<EventToGa4AdaptationResult> AdaptAsync(
        CanonicalEvent canonicalEvent,
        Guid? propertyId,
        CancellationToken ct = default)
    {
        if (canonicalEvent is null)
        {
            return EventToGa4AdaptationResult.Failed(["CanonicalEvent is null."]);
        }

        if (canonicalEvent.TenantId == Guid.Empty)
        {
            return EventToGa4AdaptationResult.Failed(["TenantId is empty."]);
        }

        if (propertyId is null || propertyId == Guid.Empty)
        {
            return EventToGa4AdaptationResult.Skipped("PropertyId is missing — store-level GA4 forwarding requires a resolved property.");
        }

        // The tenant identity comes from the event's own row (RawEvent.TenantId → CanonicalEvent.TenantId);
        // the lookup filters the property by this tenant in the same query. Another tenant's property
        // → NotFound → Skipped (credentials are not used, Ga4LastError is not written).
        var configResult = await _configReader.GetConfigAsync(canonicalEvent.TenantId, propertyId.Value, ct);

        // Invalid = credentials exist but are unusable (decryption failed, ID length
        // invalid). This is not a deliberate skip but a delivery failure: return Failed so
        // ForwardStage writes Ga4LastError and the delivery health card counts it as "Failed".
        // NotFound/Disabled (not configured) remain Skipped as deliberate skips.
        if (configResult.Status == Ga4ConfigReadStatus.Invalid)
        {
            return EventToGa4AdaptationResult.Failed(
                [configResult.Reason ?? "GA4 config is invalid or incomplete."],
                configResult.ErrorCode ?? DeliveryErrorCode.InternalError);
        }

        if (configResult.Status != Ga4ConfigReadStatus.Success)
        {
            var reason = configResult.Status switch
            {
                Ga4ConfigReadStatus.NotFound => configResult.Reason ?? "No GA4 configuration for property.",
                Ga4ConfigReadStatus.Disabled => configResult.Reason ?? "GA4 config is disabled.",
                _ => $"Unexpected config status: {configResult.Status}."
            };
            return EventToGa4AdaptationResult.Skipped(reason);
        }

        if (configResult.Config is null)
        {
            return EventToGa4AdaptationResult.Skipped("Config surface missing after success status.");
        }

        var buildResult = _requestBuilder.Build(canonicalEvent, configResult.Config.DebugMode);

        if (!buildResult.IsSuccess)
        {
            return EventToGa4AdaptationResult.Failed(
                buildResult.ValidationErrors ?? ["Request builder failed with no error details."],
                buildResult.ErrorCode ?? DeliveryErrorCode.InternalError);
        }

        if (string.IsNullOrEmpty(buildResult.RequestBodyJson))
        {
            return EventToGa4AdaptationResult.Failed(["Request builder succeeded but RequestBodyJson is empty."]);
        }

        var forwardingInput = new Ga4ForwardingInput
        {
            RequestBodyJson = buildResult.RequestBodyJson,
            MeasurementId = configResult.Config.MeasurementId,
            ApiSecret = configResult.Config.ApiSecret,
            DebugMode = configResult.Config.DebugMode
        };

        return EventToGa4AdaptationResult.Success(forwardingInput, buildResult.Warnings);
    }
}
