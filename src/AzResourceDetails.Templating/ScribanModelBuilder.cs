using System.Text.Json;
using Scriban.Runtime;

namespace AzResourceDetails.Templating;

// Builds the Scriban `model` object a generated template renders against — reusing JsonTree's
// navigation and SkuAndVersion's sku_label/version derivation (both dependency-free), adding only
// the ScriptObject/ScriptArray tree conversion that requires the Scriban package itself. Kept
// separate from those two so the recipe resolver (which never needs to render anything, only to
// decide where a value comes from) stays free of a templating-engine dependency.
public static class ScribanModelBuilder
{
    public static object? JsonToScriban(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.Object => JsonObjectToScriptObject(elem),
        JsonValueKind.Array => JsonArrayToScriptArray(elem),
        JsonValueKind.String => elem.GetString(),
        JsonValueKind.Number => elem.TryGetInt64(out var l) ? (object)l : elem.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static ScriptObject JsonObjectToScriptObject(JsonElement obj)
    {
        var so = new ScriptObject();
        foreach (var prop in obj.EnumerateObject())
        {
            so[prop.Name.ToLowerInvariant()] = JsonToScriban(prop.Value);
        }
        return so;
    }

    private static ScriptArray JsonArrayToScriptArray(JsonElement arr)
    {
        var sa = new ScriptArray();
        foreach (var item in arr.EnumerateArray())
        {
            sa.Add(JsonToScriban(item));
        }
        return sa;
    }

    private static readonly System.Text.RegularExpressions.Regex ResourceGroupFromId =
        new(@"/resourceGroups/([^/]+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Mirrors FieldRecipeResolver's ShortcutLabels handling — same fields, same derivation, so a
    // rendered preview and the recipe catalog's verification can never quietly disagree about what
    // model.location/model.resource_group actually contain.
    public static ScriptObject BuildModel(JsonElement root, string armType)
    {
        ArgumentException.ThrowIfNullOrEmpty(armType);

        var model = new ScriptObject();
        PopulateSharedFields(model, ToTemplateResource(root, armType));
        return model;
    }

    /// <summary>
    /// Same shared fields as <see cref="BuildModel(JsonElement, string)"/>, built from a caller's
    /// own decomposed resource data instead of a full ARM JSON document — see
    /// <see cref="TemplateResource"/>'s doc comment for what each field needs to mean for the two
    /// overloads to agree (verified by an equivalence test in this project alongside this method).
    /// </summary>
    public static ScriptObject BuildModel(TemplateResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var model = new ScriptObject();
        PopulateSharedFields(model, resource);
        return model;
    }

    /// <summary>
    /// Populates only the fields <see cref="TemplateRuntimeContract.SupportedModelFields"/>
    /// declares (plus <c>props</c>, the intentionally dynamic passthrough — see that class' doc
    /// comment) onto <paramref name="target"/>, and touches nothing else already on it. This is
    /// what lets a host that owns additional, vault-specific fields of its own — relationships,
    /// wiki links, a different <c>tags</c> shape, whatever — populate this library's shared fields
    /// into the SAME <see cref="ScriptObject"/> it's already building, without either side needing
    /// to know about the other's fields. <c>tags</c> is deliberately NOT one of the fields
    /// populated here: no generated template in this repo references <c>model.tags</c>, and the two
    /// known consumers of this library want two different shapes for it (a plain key/value object
    /// here, a sorted array of <c>{ key, value }</c> for AzToMd's shared footer) — picking either
    /// would just be wrong for the other, so tag representation is left entirely to the host.
    /// </summary>
    /// <remarks>
    /// Overwrite/preservation semantics, precisely: every key listed in
    /// <see cref="TemplateRuntimeContract.SupportedModelFields"/> (plus <c>props</c>) is
    /// unconditionally overwritten with the current, authoritative value derived from
    /// <paramref name="resource"/> — this is a refresh, not a merge, for those specific keys. Every
    /// other key already on <paramref name="target"/> is never read, written, or removed;
    /// <paramref name="target"/> itself is never cleared or replaced. Missing source data (a null
    /// <see cref="TemplateResource.Id"/>, an absent/<see cref="JsonValueKind.Undefined"/>
    /// <see cref="TemplateResource.Properties"/> or <see cref="TemplateResource.Sku"/>, no
    /// <see cref="TemplateResource.IdentityType"/>) is expected, ordinary input — it never throws,
    /// it just produces <see langword="null"/> or a documented fallback for the fields that depend
    /// on it (e.g. an absent <c>props</c> becomes an empty <see cref="ScriptObject"/>, not
    /// <see langword="null"/>). <paramref name="target"/> and <paramref name="resource"/> being
    /// <see langword="null"/> is different: that's an invalid call, not missing resource data, and
    /// throws <see cref="ArgumentNullException"/> immediately rather than failing confusingly deep
    /// inside a friendly-label calculation. Requires no repository files, no global initialization,
    /// and no particular call order relative to <see cref="RegionDisplayNames"/>/
    /// <see cref="TemplateFunctions"/> — <c>props</c>/<c>sku_*</c>/friendly-label fields never touch
    /// either of those.
    /// </remarks>
    public static void PopulateSharedFields(ScriptObject target, TemplateResource resource)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resource);

        var properties = resource.Properties;

        target["id"] = resource.Id;
        target["name"] = resource.Name;
        target["type"] = resource.ArmType;
        target["location"] = resource.Location;
        target["resource_group"] = resource.ResourceGroup;
        target["props"] = properties.ValueKind != JsonValueKind.Undefined ? JsonToScriban(properties) : new ScriptObject();
        target["sku_label"] = SkuAndVersion.SkuLabel(resource);
        target["sku_name"] = SkuAndVersion.SkuName(resource);
        target["sku_tier"] = SkuAndVersion.SkuTier(resource);
        target["sku_capacity"] = SkuAndVersion.SkuCapacity(resource);
        target["version"] = properties.ValueKind != JsonValueKind.Undefined
            ? SkuAndVersion.ExtractVersion(resource.ArmType, properties)
            : null;
        target["storage_replication_label"] = PortalFriendlyLabels.StorageReplicationLabel(resource);
        target["storage_account_kind_label"] = PortalFriendlyLabels.StorageAccountKindLabel(resource);
        target["storage_performance_label"] = PortalFriendlyLabels.StoragePerformanceLabel(resource);
        target["disk_storage_type_label"] = PortalFriendlyLabels.DiskStorageTypeLabel(resource);
        target["disk_security_type_label"] = PortalFriendlyLabels.DiskSecurityTypeLabel(resource);
        target["mongo_cluster_tier_label"] = PortalFriendlyLabels.MongoClusterTierLabel(resource);
        target["mongo_connectivity_method_label"] = PortalFriendlyLabels.MongoConnectivityMethodLabel(resource);
        target["mongo_authentication_label"] = PortalFriendlyLabels.MongoAuthenticationLabel(resource);
        target["mongo_storage_encryption_label"] = PortalFriendlyLabels.MongoStorageEncryptionLabel(resource);
        target["logic_workflow_definition_label"] = PortalFriendlyLabels.LogicWorkflowDefinitionLabel(resource);
        target["logic_integration_account_label"] = PortalFriendlyLabels.LogicIntegrationAccountLabel(resource);
        target["logic_workflow_type_label"] = PortalFriendlyLabels.LogicWorkflowTypeLabel(resource);
        target["appconfig_telemetry_label"] = PortalFriendlyLabels.AppConfigTelemetryLabel(resource);
        target["appconfig_pricing_tier_label"] = PortalFriendlyLabels.AppConfigPricingTierLabel(resource);
        target["aks_power_state_label"] = PortalFriendlyLabels.AksPowerStateLabel(resource);
        target["aks_cluster_operation_status_label"] = PortalFriendlyLabels.AksClusterOperationStatusLabel(resource);
        target["aks_api_server_address_label"] = PortalFriendlyLabels.AksApiServerAddressLabel(resource);
        target["aks_node_pools_label"] = PortalFriendlyLabels.AksNodePoolsLabel(resource);
        target["aks_network_configuration_label"] = PortalFriendlyLabels.AksNetworkConfigurationLabel(resource);
    }

    /// <summary>
    /// Decomposes a full ARM JSON document into a <see cref="TemplateResource"/> — the same
    /// resolution <see cref="BuildModel(JsonElement, string)"/> uses internally, exposed so a
    /// caller can inspect or reuse the decomposed shape instead of building one by hand.
    /// </summary>
    public static TemplateResource ToTemplateResource(JsonElement root, string armType)
    {
        ArgumentException.ThrowIfNullOrEmpty(armType);

        var id = JsonTree.GetString(root, "id");
        var rgMatch = id is null ? null : ResourceGroupFromId.Match(id);
        return new TemplateResource(
            Id: id,
            Name: JsonTree.GetString(root, "name"),
            ArmType: armType,
            Location: JsonTree.GetString(root, "location"),
            ResourceGroup: rgMatch is { Success: true } ? rgMatch.Groups[1].Value : null,
            Kind: JsonTree.GetString(root, "kind"),
            IdentityType: JsonTree.GetString(root, "identity", "type"),
            Properties: JsonTree.Navigate(root, "properties") ?? default(JsonElement),
            Sku: SkuAndVersion.ResolveSku(root));
    }
}
