namespace Stobio.Domain.Events;

/// <summary>
/// The SINGLE place for mapping canonical event names ↔ GA4 recommended e-commerce event names.
/// Only pairs whose names differ are listed here; names that are identical on both sides, such as
/// <c>add_to_cart</c>, <c>purchase</c>, <c>page_view</c>, are not listed and pass through unchanged.
/// <list type="bullet">
/// <item><see cref="ToGa4"/>: the name sent to the GA4 Measurement Protocol. Canonical <c>product_viewed</c>
/// becomes <c>view_item</c>, which GA4's standard reports expect.</item>
/// <item><see cref="ToCanonical"/>: converts an event arriving with a GA4 name (ikas bridge, GTM/gtag ecommerce
/// push) to the canonical name for audit / funnel / insight counting. Records (RawEvent) are not
/// migrated; since the conversion happens on read, old and new records are counted the same.</item>
/// </list>
/// Meta CAPI name mapping does not use this table (it has its own catalog, unchanged here).
/// </summary>
public static class Ga4EventNameMap
{
    private static readonly IReadOnlyDictionary<string, string> CanonicalToGa4 =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CanonicalEventNames.ProductViewed] = "view_item",
            [CanonicalEventNames.CheckoutStarted] = "begin_checkout",
        };

    private static readonly IReadOnlyDictionary<string, string> Ga4ToCanonical =
        CanonicalToGa4.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Converts a canonical name to its GA4 name; returns the name unchanged if not in the table.</summary>
    public static string ToGa4(string name) =>
        CanonicalToGa4.TryGetValue(name, out var ga4) ? ga4 : name;

    /// <summary>Converts a GA4 name to its canonical name; returns the name unchanged if not in the table.</summary>
    public static string ToCanonical(string name) =>
        Ga4ToCanonical.TryGetValue(name, out var canonical) ? canonical : name;
}
