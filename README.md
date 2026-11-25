# Stobio code samples

Selected source files from [Stobio](https://stobio.com), a server-side conversion tracking platform for e-commerce that I designed and built and run in production. Stobio receives storefront events, cleans and enriches them, and forwards them to GA4 (Measurement Protocol) and Meta (Conversions API) with deduplication against the browser tags.

The main codebase is private. These files are copied from it as they run in production, so they reference internal types and **do not build on their own**. The code is unchanged. Comments and log messages were in Turkish and are translated to English here, with internal ticket numbers removed.

I write and review this code myself and use AI coding assistants (Claude, Cursor) as part of my normal workflow.

Stack: C# / .NET, PostgreSQL, multi-tenant SaaS.

## What is in here

### `ga4-measurement-protocol/` — sending events to GA4 from the server

| File | What it does |
|---|---|
| `Ga4EventNameMap.cs` | Single source of truth that maps Stobio's event names to GA4's recommended e-commerce event names. |
| `EventToGa4MappingAdapter.cs` | Loads the store's GA4 settings (scoped to the event's own tenant) and turns a stored event into a forwarding input. |
| `Ga4MeasurementProtocolRequestBuilder.cs` | Builds the Measurement Protocol payload: client and session ids so GA4 counts users, sessions and Realtime correctly, e-commerce items and value, engagement time, and GA4's length and naming limits. |
| `Ga4ForwardingService.cs` | Sends the request with retries and exponential backoff, classifies failures (rate limit, timeout, invalid request) for a delayed retry job, and never logs the API secret or the full body. |

### `meta-conversions-api/` — sending events to Meta from the server

| File | What it does |
|---|---|
| `EventToMetaMappingAdapter.cs` | Maps an event to a Conversions API event: `event_id` shared with the browser Pixel for deduplication, SHA-256 hashed user data (email, phone, external id), order id, value and currency. |
| `MetaCapiForwardingService.cs` | Sends to the Graph API with retries, reads Meta's response (`events_received`, warnings) to catch silent rejections, and keeps the access token out of logs. |

### `site-tag-audit/`

| File | What it does |
|---|---|
| `SiteScriptAuditDetector.cs` | Rules behind Stobio's site scanner: from a snapshot of the tags found on a live store, it flags GA4 or Meta Pixel data being sent twice, tags sending to an unrelated GA4 property or Pixel, and leftover Universal Analytics. The finding titles are user-facing text and stay in Turkish, as shipped. |

### `webhook-signature/`

| File | What it does |
|---|---|
| `PaddleSignatureVerifier.cs` | Verifies billing webhooks: parses the signature header, checks the HMAC-SHA256 in constant time and rejects old timestamps to stop replays. |

### `wordpress-plugin/`

The Stobio plugin for WooCommerce, published on WordPress.org: <https://wordpress.org/plugins/stobio/>. It adds the Stobio loader to the store only after the owner enters a site key, and removes its settings on uninstall.
