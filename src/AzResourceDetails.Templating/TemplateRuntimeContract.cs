namespace AzResourceDetails.Templating;

/// <summary>
/// Machine-readable description of what a generated template is allowed to reference: the
/// top-level fields <see cref="ScribanModelBuilder.PopulateSharedFields"/> populates on
/// <c>model</c>, and the global functions <see cref="TemplateFunctions.ImportInto"/> registers.
/// A template referencing
/// anything outside this contract (other than <c>model.props.*</c>, which is an intentionally
/// dynamic passthrough of the resource's own ARM properties, or a Scriban standard-library function
/// like <c>string.capitalize</c>) will fail at render time against any host that only implements
/// this contract — see AzResourceDetailsDownloader.Core.Tests' template-contract coverage test for
/// the enforcement side of this.
/// </summary>
/// <remarks>
/// This describes only what THIS package guarantees — it is intentionally not exhaustive of what
/// any given host's templates may use. A host with its own additional fields/functions (AzToMd's
/// vault-specific relationships, wiki links, role assignments, etc.) should validate its own
/// templates against the UNION of this contract and its own extensions, e.g.
/// <c>TemplateRuntimeContract.SupportedModelFields.Concat(myOwnFields)</c> — both lists are plain
/// public <see cref="IReadOnlyList{T}"/>s specifically so a host can form that union itself without
/// needing a dedicated API here.
/// </remarks>
public static class TemplateRuntimeContract
{
    /// <summary>
    /// Bumped whenever <see cref="SupportedModelFields"/> or <see cref="SupportedFunctions"/>
    /// changes in a way a template author needs to know about (a field/function added or removed).
    /// 1.1.0: removed "tags" — no generated template in this repo ever referenced model.tags, and
    /// the two known consumers of this library want two different shapes for it (see
    /// ScribanModelBuilder.PopulateSharedFields' doc comment), so it was never actually a safe
    /// "shared" field to promise in the first place.
    /// </summary>
    public const string Version = "1.1.0";

    /// <summary>
    /// Top-level <c>model.*</c> fields a template may reference, beyond <c>model.props</c> itself
    /// (see the class doc comment — <c>props</c> is a dynamic passthrough, not enumerated here).
    /// Kept in exact sync with the keys <see cref="ScribanModelBuilder.PopulateSharedFields"/> sets.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedModelFields =
    [
        "id",
        "name",
        "type",
        "location",
        "resource_group",
        "props",
        "sku_label",
        "sku_name",
        "sku_tier",
        "sku_capacity",
        "version",
        "storage_replication_label",
        "storage_account_kind_label",
        "storage_performance_label",
        "disk_storage_type_label",
        "disk_security_type_label",
        "mongo_cluster_tier_label",
        "mongo_connectivity_method_label",
        "mongo_authentication_label",
        "mongo_storage_encryption_label",
        "logic_workflow_definition_label",
        "logic_integration_account_label",
        "logic_workflow_type_label",
        "appconfig_telemetry_label",
        "appconfig_pricing_tier_label",
        "aks_power_state_label",
        "aks_cluster_operation_status_label",
        "aks_api_server_address_label",
        "aks_node_pools_label",
        "aks_network_configuration_label",
    ];

    /// <summary>
    /// Global Scriban functions <see cref="TemplateFunctions.ImportInto"/> registers. A template
    /// may also use any built-in Scriban standard-library function (e.g. <c>string.capitalize</c>)
    /// — those aren't part of this contract since every Scriban host provides them already.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedFunctions =
    [
        "portal_bool_enabled",
        "portal_bool_yesno",
        "portal_timestamp",
        "region_display_name",
    ];
}
