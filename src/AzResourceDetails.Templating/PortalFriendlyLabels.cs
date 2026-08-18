using System.Text.Json;

namespace AzResourceDetails.Templating;

// Friendly-text lookup tables for the handful of fields where the Azure Portal runs a raw ARM enum
// through a translation table before displaying it — every entry here was read directly out of the
// live portal's own already-loaded JS (2026-08-14), not guessed, the same "live-verified, never
// guessed" bar as config/azure-locations.json (see FieldRecipeResolver's four Resolve*Shortcut
// methods that reference this class for the full provenance comment and the two-step
// fiber-walk-then-module-search technique that found them).
//
// Shared between FieldRecipeResolver (which only needs to decide where a value comes from) and
// ScribanModelBuilder (which needs to actually compute it for a rendered preview) for the same
// reason SkuAndVersion is: a rendered preview and the recipe catalog's verification must never
// quietly disagree about what model.storage_replication_label etc. actually contain.
//
// Each public method comes in two overloads — JsonElement root (a full ARM document) and
// TemplateResource (a caller's own decomposed data) — both delegating to the same private *Core
// method, so a consumer building its own TemplateResource gets byte-identical results to one
// passing a full ARM document. A handful of these (the Storage/Disk-specific ones) used to read
// sku.name/sku.tier at the document ROOT ONLY, with no properties.sku fallback — unlike
// SkuAndVersion, which already falls back. Unified onto SkuAndVersion.ResolveSku's fallback here so
// there's exactly one definition of "which sku wins" for a TemplateResource's single Sku field to
// mean the same thing everywhere; verified this changes nothing in practice by regenerating the
// full template catalog (every currently-captured Storage Account/Compute disk has a root-level
// sku, so the fallback never actually triggers for them — see AGENT.md for this session's notes).
public static class PortalFriendlyLabels
{
    private static readonly Dictionary<string, string> StorageReplicationCategoryBySkuName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Premium_LRS"] = "lrs", ["PremiumV2_LRS"] = "lrs", ["Standard_LRS"] = "lrs", ["StandardV2_LRS"] = "lrs",
            ["Standard_GRS"] = "grs", ["StandardV2_GRS"] = "grs",
            ["Standard_RAGRS"] = "ragrs",
            ["Premium_ZRS"] = "zrs", ["PremiumV2_ZRS"] = "zrs",
            ["Standard_ZRS"] = "zrs*", ["StandardV2_ZRS"] = "zrs*", // '*' = resolved against `kind` below
            ["Standard_GZRS"] = "gzrs", ["StandardV2_GZRS"] = "gzrs",
            ["Standard_RAGZRS"] = "ragzrs",
        };

    private static readonly Dictionary<string, string> StorageReplicationText =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lrs"] = "Locally redundant storage (LRS)",
            ["grs"] = "Geo-redundant storage (GRS)",
            ["ragrs"] = "Read-access geo-redundant storage (RA-GRS)",
            ["zrs"] = "Zone-redundant storage (ZRS)",
            ["zrsClassic"] = "Zone-redundant storage (ZRS classic)",
            ["gzrs"] = "Geo-zone-redundant storage (GZRS)",
            ["ragzrs"] = "Read-access geo-zone-redundant storage (RA-GZRS)",
        };

    // Storage Accounts: `Ve(sku.name, kind === "Storage")`. Returns null when sku.name isn't in the
    // known table (a new/unseen SKU, or no sku.name at all) — callers decide how to treat that.
    public static string? StorageReplicationLabel(JsonElement root) =>
        StorageReplicationLabelCore(SkuAndVersion.ResolveSku(root), JsonTree.GetString(root, "kind"));

    public static string? StorageReplicationLabel(TemplateResource resource) =>
        StorageReplicationLabelCore(resource.Sku, resource.Kind);

    private static string? StorageReplicationLabelCore(JsonElement sku, string? kind)
    {
        var skuName = JsonTree.GetString(sku, "name");
        if (skuName is null || !StorageReplicationCategoryBySkuName.TryGetValue(skuName, out var category))
        {
            return null;
        }
        if (category == "zrs*")
        {
            category = string.Equals(kind, "Storage", StringComparison.OrdinalIgnoreCase) ? "zrsClassic" : "zrs";
        }
        return StorageReplicationText[category];
    }

    private static readonly Dictionary<string, string> StorageAccountKindFormat =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Storage"] = "{0} (general purpose v1)",
            ["StorageV2"] = "{0} (general purpose v2)",
        };

    // Storage Accounts: `Le(kind, id)`. Doesn't replicate the classic-storage-account-ID branch
    // (always renders "{kind} (classic)" for a pre-ARM ID shape) — no capture this tool takes ever
    // has one, so it's not worth the complexity of reproducing.
    public static string? StorageAccountKindLabel(JsonElement root) =>
        StorageAccountKindLabelCore(JsonTree.GetString(root, "kind"));

    public static string? StorageAccountKindLabel(TemplateResource resource) =>
        StorageAccountKindLabelCore(resource.Kind);

    private static string? StorageAccountKindLabelCore(string? kind)
    {
        if (kind is null)
        {
            return null;
        }
        return StorageAccountKindFormat.TryGetValue(kind, out var format) ? string.Format(format, kind) : kind;
    }

    // Storage Accounts: `De(sku.tier, kind === "FileStorage")` — `sku.tier` (root-level, sibling to
    // `sku.name`, not the same field the Replication shortcut reads), Premium/Standard mapping to
    // literal "Premium"/"Standard" confirmed identical across every candidate resource-string object
    // found (2026-08-14; the label "Performance" itself was ambiguous across several near-duplicate
    // objects, but all of them agreed on this exact mapping). The FileStorage-kind branch renders a
    // different label ("Media Tier") with SSD/HDD text that wasn't confirmed this session — reproduced
    // here as null (unverified) rather than guessed.
    public static string? StoragePerformanceLabel(JsonElement root) =>
        StoragePerformanceLabelCore(SkuAndVersion.ResolveSku(root), JsonTree.GetString(root, "kind"));

    public static string? StoragePerformanceLabel(TemplateResource resource) =>
        StoragePerformanceLabelCore(resource.Sku, resource.Kind);

    private static string? StoragePerformanceLabelCore(JsonElement sku, string? kind)
    {
        var tier = JsonTree.GetString(sku, "tier");
        if (tier is null)
        {
            return null;
        }
        if (string.Equals(kind, "FileStorage", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return string.Equals(tier, "Premium", StringComparison.OrdinalIgnoreCase) ? "Premium" : "Standard";
    }

    private static readonly Dictionary<string, string> DiskStorageTypeText =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard_LRS"] = "Standard HDD LRS",
            ["StandardSSD_LRS"] = "Standard SSD LRS",
            ["StandardSSD_ZRS"] = "Standard SSD ZRS",
            ["UltraSSD_LRS"] = "Ultra disk LRS",
            ["Premium_LRS"] = "Premium SSD LRS",
            ["Premium_ZRS"] = "Premium SSD ZRS",
            ["PremiumV2_LRS"] = "Premium SSD v2 LRS",
            ["Standard_ZRS"] = "Zone-redundant",
        };

    // Compute/disks (and snapshots, same shape): `Hs(sku.name)`.
    public static string? DiskStorageTypeLabel(JsonElement root) => DiskStorageTypeLabelCore(SkuAndVersion.ResolveSku(root));

    public static string? DiskStorageTypeLabel(TemplateResource resource) => DiskStorageTypeLabelCore(resource.Sku);

    private static string? DiskStorageTypeLabelCore(JsonElement sku)
    {
        var skuName = JsonTree.GetString(sku, "name");
        return skuName is not null && DiskStorageTypeText.TryGetValue(skuName, out var text) ? text : null;
    }

    private static readonly Dictionary<string, string> DiskSecurityTypeText =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard"] = "Standard",
            ["TrustedLaunch"] = "Trusted launch",
            ["TrustedLaunchSupported"] = "Trusted launch supported",
            ["ConfidentialVM_VMGuestStateOnlyEncryptedWithPlatformKey"] = "Confidential",
            ["ConfidentialVM_DiskEncryptedWithPlatformKey"] = "Confidential",
            ["ConfidentialVM_DiskEncryptedWithCustomerKey"] = "Confidential",
        };

    // Compute/disks: `st(properties.securityProfile.securityType)` — a disk with no securityProfile
    // at all falls back to "Standard" in the portal's own code, reproduced here rather than null.
    public static string DiskSecurityTypeLabel(JsonElement root) =>
        DiskSecurityTypeLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string DiskSecurityTypeLabel(TemplateResource resource) => DiskSecurityTypeLabelCore(resource.Properties);

    private static string DiskSecurityTypeLabelCore(JsonElement properties)
    {
        var securityType = JsonTree.GetString(properties, "securityProfile", "securityType");
        return securityType is not null && DiskSecurityTypeText.TryGetValue(securityType, out var text)
            ? text
            : "Standard";
    }

    // DocumentDB/mongoClusters: `_t(properties.compute.tier)` — a per-tier {vCores, ramInGb} lookup
    // table (`Ot` in the portal's cosmos bundle, 2026-08-14) formatted as "{tier} tier, {N} vCores,
    // {M} GiB RAM", except the two known autoscale entries which drop the vCores/RAM numbers entirely
    // ("{tier} tier" only) and "Free", which is a fixed literal. A tier not in this table returns
    // null rather than guessing — the source's own fallback ("Unsupported cluster tier: {e}") isn't
    // reproduced since a template shouldn't echo an error string as if it were data.
    private static readonly Dictionary<string, (int VCores, int RamGb, bool Autoscale)> MongoClusterTierSpecs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["M10"] = (1, 2, false), ["M20"] = (2, 4, false), ["M25"] = (2, 8, false), ["M30"] = (2, 8, false),
            ["M40"] = (4, 16, false), ["M50"] = (8, 32, false), ["M60"] = (16, 64, false), ["M80"] = (32, 128, false),
            ["M200"] = (64, 256, false), ["M200-Autoscale"] = (32, 256, true),
            ["M300"] = (48, 384, false), ["M400"] = (64, 432, false),
        };

    public static string? MongoClusterTierLabel(JsonElement root) =>
        MongoClusterTierLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string? MongoClusterTierLabel(TemplateResource resource) => MongoClusterTierLabelCore(resource.Properties);

    private static string? MongoClusterTierLabelCore(JsonElement properties)
    {
        var tier = JsonTree.GetString(properties, "compute", "tier");
        if (tier is null)
        {
            return null;
        }
        if (string.Equals(tier, "Free", StringComparison.OrdinalIgnoreCase))
        {
            return "Free tier";
        }
        if (!MongoClusterTierSpecs.TryGetValue(tier, out var spec))
        {
            return null;
        }
        return spec.Autoscale ? $"{tier} tier" : $"{tier} tier, {spec.VCores} vCores, {spec.RamGb} GiB RAM";
    }

    // DocumentDB/mongoClusters: the `Enabled -> publicAccess / else -> privateAccessTabName` ternary
    // in `customizeResourceFields`, both resource-string values read verbatim (2026-08-14).
    public static string? MongoConnectivityMethodLabel(JsonElement root) =>
        MongoConnectivityMethodLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string? MongoConnectivityMethodLabel(TemplateResource resource) =>
        MongoConnectivityMethodLabelCore(resource.Properties);

    private static string? MongoConnectivityMethodLabelCore(JsonElement properties)
    {
        var publicNetworkAccess = JsonTree.GetString(properties, "publicNetworkAccess");
        if (publicNetworkAccess is null)
        {
            return null;
        }
        return string.Equals(publicNetworkAccess, "Enabled", StringComparison.OrdinalIgnoreCase)
            ? "Public access"
            : "Private access";
    }

    // DocumentDB/mongoClusters: `properties.authConfig.allowedModes` (an array of enum strings) run
    // through a 3-way membership check. The source's own 4th branch (neither mode present) returns an
    // unrendered glyph placeholder, not real text — reproduced here as null rather than guessed.
    public static string? MongoAuthenticationLabel(JsonElement root) =>
        MongoAuthenticationLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string? MongoAuthenticationLabel(TemplateResource resource) => MongoAuthenticationLabelCore(resource.Properties);

    private static string? MongoAuthenticationLabelCore(JsonElement properties)
    {
        var modes = JsonTree.Navigate(properties, "authConfig", "allowedModes");
        if (modes is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }
        var set = array.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            .Where(s => s is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasEntra = set.Contains("MicrosoftEntraID");
        var hasNative = set.Contains("NativeAuth");
        return (hasEntra, hasNative) switch
        {
            (true, true) => "Native DocumentDB and Entra ID",
            (false, true) => "Native DocumentDB only",
            (true, false) => "Microsoft Entra ID only",
            _ => null,
        };
    }

    // DocumentDB/mongoClusters: `identity.type === "UserAssigned" ? cmkLabel : smkLabel` — root-level
    // `identity`, not under properties.*, and unconditional (no identity block at all still resolves
    // to the "Service-managed key" default, matching the source's own missing-value behavior).
    public static string MongoStorageEncryptionLabel(JsonElement root) =>
        MongoStorageEncryptionLabelCore(JsonTree.GetString(root, "identity", "type"));

    public static string MongoStorageEncryptionLabel(TemplateResource resource) =>
        MongoStorageEncryptionLabelCore(resource.IdentityType);

    private static string MongoStorageEncryptionLabelCore(string? identityType) =>
        string.Equals(identityType, "UserAssigned", StringComparison.OrdinalIgnoreCase)
            ? "Customer-managed key"
            : "Service-managed key";

    // Logic/workflows: `Fe(t)` — counts `properties.definition.triggers`/`.actions` object keys,
    // formats each count through a singular/plural resource string, joins with a fixed "{0}, {1}"
    // template (all read 2026-08-14).
    public static string? LogicWorkflowDefinitionLabel(JsonElement root) =>
        LogicWorkflowDefinitionLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string? LogicWorkflowDefinitionLabel(TemplateResource resource) =>
        LogicWorkflowDefinitionLabelCore(resource.Properties);

    private static string? LogicWorkflowDefinitionLabelCore(JsonElement properties)
    {
        var definition = JsonTree.Navigate(properties, "definition");
        if (definition is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }
        var triggers = JsonTree.Navigate(properties, "definition", "triggers");
        var actions = JsonTree.Navigate(properties, "definition", "actions");
        var triggerCount = triggers is { ValueKind: JsonValueKind.Object } t ? t.EnumerateObject().Count() : 0;
        var actionCount = actions is { ValueKind: JsonValueKind.Object } a ? a.EnumerateObject().Count() : 0;
        var triggerText = triggerCount == 1 ? "1 trigger" : $"{triggerCount} triggers";
        var actionText = actionCount == 1 ? "1 action" : $"{actionCount} actions";
        return $"{triggerText}, {actionText}";
    }

    // Logic/workflows: `Pe(t)` — `properties.integrationAccount.id` existence gates whether anything
    // shows at all; when present, the rendered text is `.name`, not the id (read 2026-08-14).
    public static string LogicIntegrationAccountLabel(JsonElement root) =>
        LogicIntegrationAccountLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string LogicIntegrationAccountLabel(TemplateResource resource) =>
        LogicIntegrationAccountLabelCore(resource.Properties);

    private static string LogicIntegrationAccountLabelCore(JsonElement properties)
    {
        var id = JsonTree.GetString(properties, "integrationAccount", "id");
        if (id is null)
        {
            return "--";
        }
        return JsonTree.GetString(properties, "integrationAccount", "name") ?? "--";
    }

    // Logic/workflows: `Qe(t)` — only the common "no agent configured" default is reproduced here
    // ("Stateful", read 2026-08-14); the Autonomous-agent/Conversational-agent branches call a further
    // opaque helper (`De.YF`) not traced this session, so a workflow with `properties.definition.
    // metadata.agentType` set returns null (unresolved) rather than a guess.
    public static string? LogicWorkflowTypeLabel(JsonElement root) =>
        LogicWorkflowTypeLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string? LogicWorkflowTypeLabel(TemplateResource resource) => LogicWorkflowTypeLabelCore(resource.Properties);

    private static string? LogicWorkflowTypeLabelCore(JsonElement properties)
    {
        var agentType = JsonTree.GetString(properties, "definition", "metadata", "agentType");
        return agentType is null ? "Stateful" : null;
    }

    // AppConfiguration/configurationStores: "Pricing tier" is NOT the generic combined SkuLabel
    // shape — it's `n!==Premium ? format("{0} (Click to upgrade)", Jp(n)) : premiumSku`, `n` = raw
    // sku.name, `Jp` a friendly-casing lookup (`freeSku`/`standardSku`/`developerSku`/`premiumSku`
    // resource strings, all read 2026-08-14). Only reached via FieldRecipeResolver's dedicated
    // AppConfig-scoped dispatch — "Pricing tier" is shared by a dozen other resource types with
    // completely different formats, so this can't be a global label-based shortcut the way
    // Replication/Account kind etc. are.
    private static readonly Dictionary<string, string> AppConfigSkuNameText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["free"] = "Free",
        ["standard"] = "Standard",
        ["developer"] = "Developer",
    };

    public static string? AppConfigPricingTierLabel(JsonElement root) => AppConfigPricingTierLabelCore(SkuAndVersion.ResolveSku(root));

    public static string? AppConfigPricingTierLabel(TemplateResource resource) => AppConfigPricingTierLabelCore(resource.Sku);

    private static string? AppConfigPricingTierLabelCore(JsonElement sku)
    {
        var skuName = JsonTree.GetString(sku, "name");
        if (skuName is null)
        {
            return null;
        }
        if (string.Equals(skuName, "premium", StringComparison.OrdinalIgnoreCase))
        {
            return "Premium";
        }
        return AppConfigSkuNameText.TryGetValue(skuName, out var text) ? $"{text} (Click to upgrade)" : null;
    }

    // AppConfiguration/configurationStores: `Telemetry` — an existence check on
    // `properties.telemetry.resourceId`, not a literal boolean property, so it doesn't fit
    // PortalFieldKnowledge.BooleanBackedLabels' "compare a JSON true/false leaf" shape (read 2026-08-14).
    public static string AppConfigTelemetryLabel(JsonElement root) =>
        AppConfigTelemetryLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string AppConfigTelemetryLabel(TemplateResource resource) => AppConfigTelemetryLabelCore(resource.Properties);

    private static string AppConfigTelemetryLabelCore(JsonElement properties)
    {
        var resourceId = JsonTree.GetString(properties, "telemetry", "resourceId");
        return string.IsNullOrEmpty(resourceId) ? "Disabled" : "Enabled";
    }

    // ContainerService/ManagedClusters (AKS) — all five below come from the same portal helper
    // module (webpack module 5379, function `W8`/`g`) and share its "-" empty-value fallback
    // (`ContainerServiceResources.noContent`, read 2026-08-14).
    private const string AksNoContent = "-";

    // `w(properties.powerState.code)` — a case-insensitive compare against a small fixed enum, whose
    // resource-string table happens to be a pure identity mapping (every value displays as its own
    // canonical casing) for the values actually reachable at cluster level.
    private static readonly HashSet<string> AksPowerStates =
        new(StringComparer.OrdinalIgnoreCase) { "Running", "Stopped", "Starting", "Stopping", "Deallocated" };

    public static string AksPowerStateLabel(JsonElement root) =>
        AksPowerStateLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string AksPowerStateLabel(TemplateResource resource) => AksPowerStateLabelCore(resource.Properties);

    private static string AksPowerStateLabelCore(JsonElement properties)
    {
        var code = JsonTree.GetString(properties, "powerState", "code");
        return code is not null && AksPowerStates.Contains(code) ? Canonicalize(code, AksPowerStates) : AksNoContent;
    }

    // `he.MF(properties.provisioningState)` — same identity-mapping shape as powerState above; the
    // source's own switch also covers two NodePool-only states (Scaling/UpgradingNodeImageVersion)
    // that never appear in the cluster-level provisioningState this label actually reads, so they're
    // not included here.
    private static readonly HashSet<string> AksProvisioningStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Canceled", "Creating", "Deleting", "Failed", "Starting", "Stopping", "Succeeded", "Updating", "Upgrading",
    };

    public static string AksClusterOperationStatusLabel(JsonElement root) =>
        AksClusterOperationStatusLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string AksClusterOperationStatusLabel(TemplateResource resource) =>
        AksClusterOperationStatusLabelCore(resource.Properties);

    private static string AksClusterOperationStatusLabelCore(JsonElement properties)
    {
        var state = JsonTree.GetString(properties, "provisioningState");
        return state is not null && AksProvisioningStates.Contains(state)
            ? Canonicalize(state, AksProvisioningStates)
            : AksNoContent;
    }

    private static string Canonicalize(string value, HashSet<string> canonicalForms) =>
        canonicalForms.First(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));

    // `properties.fqdn || properties.privateFQDN`, direct passthrough.
    public static string AksApiServerAddressLabel(JsonElement root) =>
        AksApiServerAddressLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string AksApiServerAddressLabel(TemplateResource resource) => AksApiServerAddressLabelCore(resource.Properties);

    private static string AksApiServerAddressLabelCore(JsonElement properties) =>
        JsonTree.GetString(properties, "fqdn")
        ?? JsonTree.GetString(properties, "privateFQDN")
        ?? AksNoContent;

    // `C(properties.agentPoolProfiles)` — counts total pools and pools whose own
    // `provisioningState === "Failed"`, picks one of four singular/plural/failed-suffixed templates.
    public static string AksNodePoolsLabel(JsonElement root) =>
        AksNodePoolsLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string AksNodePoolsLabel(TemplateResource resource) => AksNodePoolsLabelCore(resource.Properties);

    private static string AksNodePoolsLabelCore(JsonElement properties)
    {
        var pools = JsonTree.Navigate(properties, "agentPoolProfiles");
        if (pools is not { ValueKind: JsonValueKind.Array } array)
        {
            return AksNoContent;
        }
        var total = 0;
        var failed = 0;
        foreach (var pool in array.EnumerateArray())
        {
            total++;
            if (JsonTree.GetString(pool, "provisioningState") == "Failed")
            {
                failed++;
            }
        }
        if (failed > 0)
        {
            return total == 1 ? $"1 node pool - {failed} failed" : $"{total} node pools - {failed} failed";
        }
        return total == 1 ? "1 node pool" : $"{total} node pools";
    }

    // `h(e)` — `properties.networkProfile.networkPlugin`, and when it's "azure", a further check of
    // `networkPluginMode` ("overlay") or any agent pool having a `podSubnetID` set. Reproduces the
    // current (feature-flags-stable/GA) portal behavior observed live 2026-08-14, not the older
    // pre-overlay fallback text the source falls back to when those flags are off.
    public static string? AksNetworkConfigurationLabel(JsonElement root) =>
        AksNetworkConfigurationLabelCore(JsonTree.Navigate(root, "properties") ?? default(JsonElement));

    public static string? AksNetworkConfigurationLabel(TemplateResource resource) =>
        AksNetworkConfigurationLabelCore(resource.Properties);

    private static string? AksNetworkConfigurationLabelCore(JsonElement properties)
    {
        var plugin = JsonTree.GetString(properties, "networkProfile", "networkPlugin");
        if (plugin is null)
        {
            return null;
        }
        if (string.Equals(plugin, "kubenet", StringComparison.OrdinalIgnoreCase))
        {
            return "kubenet";
        }
        if (!string.Equals(plugin, "azure", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var mode = JsonTree.GetString(properties, "networkProfile", "networkPluginMode");
        if (string.Equals(mode, "overlay", StringComparison.OrdinalIgnoreCase))
        {
            return "Azure CNI Overlay";
        }
        var pools = JsonTree.Navigate(properties, "agentPoolProfiles");
        var hasPodSubnet = pools is { ValueKind: JsonValueKind.Array } array
            && array.EnumerateArray().Any(p => JsonTree.GetString(p, "podSubnetID") is not null);
        return hasPodSubnet ? "Azure CNI Pod Subnet" : null;
    }
}
