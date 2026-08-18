using System.Text.Json;

namespace AzResourceDetails.Templating;

/// <summary>
/// Decomposed ARM resource data — the neutral input shape for a consumer that doesn't retain a
/// single complete ARM JSON document (e.g. AzToMd's TenantNode, which already splits a resource
/// into separate fields). <see cref="ScribanModelBuilder.BuildModel(JsonElement, string)"/> builds
/// one of these internally from a full document; a consumer with its own decomposed data can
/// construct one directly instead of synthesizing JSON just to satisfy this library.
/// </summary>
/// <remarks>
/// <see cref="Sku"/> is deliberately a single, already-resolved element, not "wherever an ARM
/// document happens to put it" — some resource types nest their sku object under <c>properties</c>
/// instead of at the document root (Key Vault, Application Gateway, ...). A caller building this
/// from a full ARM document should resolve that ambiguity the same way
/// <see cref="SkuAndVersion.ResolveSku"/> does (root-level <c>sku</c> if present, else
/// <c>properties.sku</c>) so behavior matches the JsonElement-based overloads exactly — this is
/// exactly what <see cref="ScribanModelBuilder.BuildModel(JsonElement, string)"/> does internally.
/// <see cref="Properties"/> and <see cref="Sku"/> use <see cref="JsonValueKind.Undefined"/> (a
/// default <see langword="default"/> <see cref="JsonElement"/>) to mean "not present," the same
/// convention <see cref="JsonTree"/>'s own navigation already uses.
/// </remarks>
public sealed record TemplateResource(
    string? Id,
    string? Name,
    string ArmType,
    string? Location,
    string? ResourceGroup,
    string? Kind,
    string? IdentityType,
    JsonElement Properties,
    JsonElement Sku);
