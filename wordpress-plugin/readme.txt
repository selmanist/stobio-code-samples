=== Stobio — Server-Side Tracking for WooCommerce (GA4 + Meta CAPI) ===
Contributors: stobio
Tags: woocommerce, ga4, conversions api, meta pixel, server-side tracking
Requires at least: 5.7
Tested up to: 7.0
Requires PHP: 7.4
Stable tag: 1.0.1
License: GPLv2 or later
License URI: https://www.gnu.org/licenses/gpl-2.0.html

Server-side tracking for WooCommerce — stop losing ad conversions. Stobio receives your store's events and forwards them reliably to GA4 & Meta.

== Description ==

**Stobio** is a tracking reliability platform for store owners — no developer needed. Ad blockers, browser privacy features and fragile tag setups silently eat your conversion data; Stobio catches your events server-side, shows you what is actually being delivered to GA4 and Meta, and alerts you when something breaks.

This plugin is the official WordPress/WooCommerce connector. It does one thing well: after you enter your Stobio site key, it places the official Stobio loader snippet on your site front end — the exact same two-tag snippet you would otherwise paste into your theme by hand.

**What you get with Stobio:**

* Server-side event delivery to GA4 (Measurement Protocol) and Meta (Conversions API)
* A delivery-health dashboard: what reached GA4/Meta, what failed, and why
* Tag audit: detects duplicate or legacy tags on your pages
* Alerts when tracking breaks — before your reports go dark
* Consent-aware by design: honors Google Consent Mode v2 and the `window.stobioConsent` signal; when consent is denied, identifiers are not collected

**How it works with WooCommerce:** the Stobio loader observes your existing `dataLayer` events (the ones your GTM/GA4 e-commerce setup already pushes) and mirrors them to Stobio server-side. It adds no tracking of its own on top of what your site already emits, and it does not slow your pages — the loader is a small script loaded with `defer`.

= External service (please read) =

Stobio is a **paid external service** (plans from $49/month, 14-day free trial — no card required). This plugin is useless without a Stobio account.

When configured, your site loads the Stobio loader script from `api.stobio.com` and sends the observed events to the Stobio API, where they are processed and forwarded to the destinations **you** configure (GA4 / Meta). No data is sent anywhere until you save your site key in the plugin settings.

* Service: [stobio.com](https://stobio.com)
* Terms of service: [stobio.com/legal/en/terms.html](https://stobio.com/legal/en/terms.html)
* Privacy policy: [stobio.com/legal/en/privacy.html](https://stobio.com/legal/en/privacy.html)

== Installation ==

1. Install and activate the plugin.
2. Create a Stobio account at [stobio.com](https://stobio.com) (14-day free trial) and add your store.
3. In your Stobio panel, open **Integrations** and copy the tracking snippet (or just the `data-tenant` value).
4. In WordPress, go to **Settings → Stobio**, paste the snippet or the key, and click **Connect**.

That's it — event delivery shows up in your Stobio dashboard within minutes of real traffic.

== Frequently Asked Questions ==

= Do I need a Stobio account? =

Yes. Stobio is a paid service with a 14-day free trial; the plugin only connects your site to it.

= Why does GA4 show fewer orders than WooCommerce? =

Ad blockers, iOS/Safari privacy features and consent banners stop browser-side tags from firing, so GA4 and Meta typically miss a large share of your real conversions. Stobio catches the events server-side, delivers them over the GA4 Measurement Protocol and Meta Conversions API, and shows you exactly what was delivered and what failed.

= Does it work with WooCommerce? =

Yes — WooCommerce stores are the main use case. The plugin observes the e-commerce `dataLayer` events your existing GA4/GTM setup already pushes (view_item, add_to_cart, purchase and so on) and mirrors them to Stobio server-side.

= Will it slow down my site? =

No. The loader is a small script added with `defer`, so it never blocks page rendering.

= Does it work without Google Tag Manager? =

Stobio observes `dataLayer` events. Most GA4/GTM e-commerce setups (including popular WooCommerce GA4 plugins) already push these. If your site pushes no `dataLayer` events at all, connect a GA4/GTM plugin first — Stobio will then see the same events.

= What about GDPR / consent? =

The loader honors Google Consent Mode v2 signals and the `window.stobioConsent` object set by your consent banner. When analytics consent is denied, no visitor identifiers are collected. See the [privacy policy](https://stobio.com/legal/en/privacy.html) for details.

= Can I temporarily disable the snippet without deactivating the plugin? =

Yes — either clear the site key in **Settings → Stobio**, or return `false` from the `stobio_should_inject` filter.

== Screenshots ==

1. Settings page — connect your site with the Stobio site key.
2. Account overview — delivery health for all your stores at a glance (service).
3. Store dashboard — what reached GA4/Meta, what failed, and why (service).
4. Reports — sessions, conversions and revenue measured server-side (service).
5. Insights — findings about your tracking setup, explained in plain language (service).
6. Integrations — where you copy the tracking snippet and site key (service).

== Changelog ==

= 1.0.1 =
* Listing update only — no functional changes: refreshed description, pricing and screenshots; clarified WooCommerce usage in the FAQ.

= 1.0.0 =
* Initial release: official Stobio loader snippet injection with settings page, full-snippet paste convenience and `stobio_should_inject` filter.

== Upgrade Notice ==

= 1.0.1 =
Listing/readme update only — no code changes.

= 1.0.0 =
Initial release.
