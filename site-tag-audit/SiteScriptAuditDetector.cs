using Stobio.Domain.Audit;

namespace Stobio.Application.Audit;

/// <summary>
/// Default <see cref="ISiteScriptAuditDetector"/> implementation. Returns an empty list if there is no snapshot;
/// if a snapshot exists, produces issues according to the severity map.
/// </summary>
public sealed class SiteScriptAuditDetector : ISiteScriptAuditDetector
{
    private readonly IPropertySiteScriptSnapshotRepository _repository;

    public SiteScriptAuditDetector(IPropertySiteScriptSnapshotRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<IReadOnlyList<SiteScriptAuditIssue>> DetectAsync(
        Guid propertyId,
        Guid tenantId,
        string? configuredGa4MeasurementId,
        string? configuredMetaPixelId,
        bool metaBrowserEventsEnabled,
        CancellationToken ct = default)
    {
        var snapshot = await _repository.GetByPropertyIdAsync(propertyId, tenantId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Array.Empty<SiteScriptAuditIssue>();
        }

        var issues = new List<SiteScriptAuditIssue>();

        // ---- GA4 duplicate / unrelated ----
        var configuredGa4 = string.IsNullOrWhiteSpace(configuredGa4MeasurementId)
            ? null
            : configuredGa4MeasurementId.Trim();

        var detectedGa4Set = new HashSet<string>(snapshot.DetectedGa4Ids, StringComparer.OrdinalIgnoreCase);
        var unrelatedGa4 = new List<string>();

        if (configuredGa4 is not null && detectedGa4Set.Contains(configuredGa4))
        {
            issues.Add(new SiteScriptAuditIssue
            {
                RuleCode = SiteScriptAuditRuleCodes.Ga4ClientSideDuplicate,
                Title = "GA4'e çift veri gönderiliyor",
                Message =
                    $"Sitenizdeki GA4 etiketi ({configuredGa4}) ile Stobio aynı property'ye event gönderiyor. " +
                    "Bu durum GA4 raporlarında ciroyu ve dönüşümleri iki katı gösterir. " +
                    "Sitenizdeki GA4 etiketini kaldırmanızı öneririz.",
                Severity = AuditSeverity.Critical
            });
        }

        foreach (var id in snapshot.DetectedGa4Ids)
        {
            if (configuredGa4 is not null &&
                string.Equals(id, configuredGa4, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            unrelatedGa4.Add(id);
        }

        if (unrelatedGa4.Count > 0)
        {
            issues.Add(new SiteScriptAuditIssue
            {
                RuleCode = SiteScriptAuditRuleCodes.UnrelatedGa4Detected,
                Title = "Stobio dışında bir GA4 hesabına da gönderim var",
                Message =
                    $"Sitenizde forward etmediğiniz başka bir GA4 property'si ({string.Join(", ", unrelatedGa4)}) " +
                    "tespit ettik. Kasıtlı çoklu izleme yapıyorsanız bir aksiyon gerekmez; aksi halde bu etiketi " +
                    "kaldırmak veya Stobio forwarding'ine eklemek isteyebilirsiniz.",
                Severity = AuditSeverity.Info
            });
        }

        // ---- Meta Pixel duplicate / unrelated ----
        var configuredMeta = string.IsNullOrWhiteSpace(configuredMetaPixelId)
            ? null
            : configuredMetaPixelId.Trim();

        var detectedMetaSet = new HashSet<string>(snapshot.DetectedMetaPixelIds, StringComparer.Ordinal);
        var unrelatedMeta = new List<string>();

        if (configuredMeta is not null && detectedMetaSet.Contains(configuredMeta))
        {
            // Dedup design: the pixel's `fbq('init')` STAYS on the site; the loader issues `fbq('track')`
            // with the same event_id and Meta deduplicates. We do not advise removing the pixel.
            // With the flag on, this is the expected setup (Info); with it off, the pixel + CAPI double-counting
            // risk remains (Warning) and the action is "turn on the setting + remove track calls from the theme".
            if (metaBrowserEventsEnabled)
            {
                issues.Add(new SiteScriptAuditIssue
                {
                    RuleCode = SiteScriptAuditRuleCodes.MetaPixelClientSideDuplicate,
                    Title = "Meta Pixel sitede, tekilleştirme açık",
                    Message =
                        $"Pixel init'iniz ({configuredMeta}) sitede; Stobio olayları aynı kimlikle bildiriyor, " +
                        "Meta tekilleştiriyor. Temanızda fbq('track', …) çağrısı kaldıysa kaldırın, fbq('init') kalsın.",
                    Severity = AuditSeverity.Info
                });
            }
            else
            {
                issues.Add(new SiteScriptAuditIssue
                {
                    RuleCode = SiteScriptAuditRuleCodes.MetaPixelClientSideDuplicate,
                    Title = "Meta'ya çift veri gidebilir",
                    Message =
                        $"Sitenizdeki Meta Pixel ({configuredMeta}) ile Stobio aynı pixel'e olay gönderiyor; " +
                        "tarayıcı olayları ayarı kapalı olduğu için Meta bunları tekilleştiremiyor ve raporlar şişebilir. " +
                        "Entegrasyonlar sayfasındaki Meta kartından tarayıcı olayları ayarını açın ve temanızdaki " +
                        "fbq('track', …) çağrılarını kaldırın; fbq('init') kalsın, pixel'i silmeyin.",
                    Severity = AuditSeverity.Warning
                });
            }
        }

        foreach (var id in snapshot.DetectedMetaPixelIds)
        {
            if (configuredMeta is not null &&
                string.Equals(id, configuredMeta, StringComparison.Ordinal))
            {
                continue;
            }
            unrelatedMeta.Add(id);
        }

        if (unrelatedMeta.Count > 0)
        {
            issues.Add(new SiteScriptAuditIssue
            {
                RuleCode = SiteScriptAuditRuleCodes.UnrelatedMetaDetected,
                Title = "Stobio dışında bir Meta Pixel'e de gönderim var",
                Message =
                    $"Sitenizde forward etmediğiniz başka bir Meta Pixel ({string.Join(", ", unrelatedMeta)}) " +
                    "tespit ettik. Kasıtlı çoklu izleme yapıyorsanız bir aksiyon gerekmez; aksi halde bu pixel'i " +
                    "kaldırmak veya Stobio forwarding'ine eklemek isteyebilirsiniz.",
                Severity = AuditSeverity.Info
            });
        }

        // ---- Legacy UA ----
        if (snapshot.HasLegacyUa)
        {
            issues.Add(new SiteScriptAuditIssue
            {
                RuleCode = SiteScriptAuditRuleCodes.LegacyUaDetected,
                Title = "Eski Google Analytics (UA) tespit edildi",
                Message =
                    "Sitenizde Google'ın Temmuz 2023'te kapattığı eski Universal Analytics scripti var. " +
                    "Bu script artık veri toplamıyor — kaldırabilirsiniz.",
                Severity = AuditSeverity.Warning
            });
        }

        // GTM does not produce issues for now (opaque container; informational level is sufficient for the pilot).

        return issues;
    }
}
