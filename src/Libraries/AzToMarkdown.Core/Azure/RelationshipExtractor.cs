using System.Text.Json;
using AzToMarkdown.Core.Models;

namespace AzToMarkdown.Core.Azure;

/// <summary>
/// Pure in-memory relationship builder.  Takes the flat list of <see cref="TenantNode"/>
/// objects produced by <see cref="TenantEnumerator"/> and emits directed edges into a
/// <see cref="TenantGraph"/> by parsing already-fetched JSON properties.
/// No I/O — zero ARG queries. Unresolvable backend addresses do not produce edges.
/// </summary>
public sealed class RelationshipExtractor
{
    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fully-populated <see cref="TenantGraph"/> from the given flat node list.
    /// All nodes are added first, then relationships are extracted in a single pass.
    /// </summary>
    public TenantGraph Build(IReadOnlyList<TenantNode> nodes)
    {
        var graph = new TenantGraph();

        // Pass 1 — index all nodes (required before edge resolution).
        foreach (var node in nodes)
            graph.AddNode(node);

        // Pass 2 — extract relationships per resource type.
        // Wrapped per-node so a malformed properties bag on any single resource
        // cannot abort the entire graph build.
        foreach (var node in nodes)
        {
            if (node.Properties.ValueKind == JsonValueKind.Undefined) continue;
            try
            {
                ExtractNodeEdges(node, graph);
            }
            catch
            {
                // Skip silently — prefer a complete graph over a crash on one bad resource.
            }
        }

        return graph;
    }

    private static void ExtractNodeEdges(TenantNode node, TenantGraph graph)
    {
        switch (node.Type)
        {
            case "microsoft.network/networkinterfaces":
                ExtractNicEdges(node, graph);
                break;

            case "microsoft.network/publicipaddresses":
                ExtractPublicIpEdges(node, graph);
                break;

            case "microsoft.network/applicationgateways":
                ExtractAppGatewayEdges(node, graph);
                break;

            case "microsoft.network/frontdoors":
                ExtractFrontDoorEdges(node, graph);
                break;

            case "microsoft.cdn/profiles":
                ExtractAfdEdges(node, graph);
                break;

            case "microsoft.compute/virtualmachines":
                ExtractVmEdges(node, graph);
                break;

            case "microsoft.compute/virtualmachinescalesets":
                ExtractVmssEdges(node, graph);
                break;

            case "microsoft.containerservice/managedclusters":
                ExtractAksEdges(node, graph);
                break;

            case "microsoft.app/containerapps":
                ExtractContainerAppEdges(node, graph);
                break;

            case "microsoft.web/sites":
                ExtractWebAppEdges(node, graph);
                break;

            case "microsoft.network/privateendpoints":
                ExtractPrivateEndpointEdges(node, graph);
                break;

            case "microsoft.network/loadbalancers":
                    ExtractLoadBalancerEdges(node, graph);
                    break;

                case "microsoft.authorization/roleassignments":
                    ExtractRoleAssignmentEdges(node, graph);
                    break;

                default:
                    // VM extensions → link to parent VM (strip last 2 path segments)
                    if (node.Type.StartsWith("microsoft.compute/virtualmachines/"))
                    {
                        var parentVmId = StripChildSegments(node.ResourceId, 2);
                        if (!string.IsNullOrEmpty(parentVmId))
                            graph.AddEdge(parentVmId, node.ResourceId, "extension");
                    }
                    // DNS record types: microsoft.network/dnszones/a, /cname, /txt …
                    else if (node.Type.StartsWith("microsoft.network/dnszones/")
                          || node.Type.StartsWith("microsoft.network/privatednszones/"))
                        ExtractDnsRecordEdges(node, graph);
                    // Generic child-resource fallback — any type with two or more '/' segments
                    // (e.g. microsoft.web/sites/slots, microsoft.sql/servers/databases,
                    //  microsoft.cdn/profiles/customdomains, microsoft.automation/automationaccounts/runbooks)
                    else if (node.Type.Count(c => c == '/') >= 2)
                    {
                        var parentId = StripChildSegments(node.ResourceId, 2);
                        if (!string.IsNullOrEmpty(parentId))
                            graph.AddEdge(parentId, node.ResourceId, "child resource");
                    }
                    break;
            }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-type extractors
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>NIC → Subnet (ipConfigurations[*].properties.subnet.id) and NIC → VM.</summary>
    private static void ExtractNicEdges(TenantNode nic, TenantGraph graph)
    {
        var props = nic.Properties;

        // NIC → VM
        if (props.TryGetProperty("virtualMachine", out var vm)
            && vm.TryGetProperty("id", out var vmId))
        {
            var vmIdStr = vmId.GetString() ?? "";
            if (!string.IsNullOrEmpty(vmIdStr))
                graph.AddEdge(vmIdStr, nic.ResourceId, "network interface");
        }

        // NIC → Subnet
        if (!props.TryGetProperty("ipConfigurations", out var cfgs)
            || cfgs.ValueKind != JsonValueKind.Array) return;

        foreach (var cfg in cfgs.EnumerateArray())
        {
            if (!cfg.TryGetProperty("properties", out var cfgProps)) continue;

            if (cfgProps.TryGetProperty("subnet", out var subnet)
                && subnet.TryGetProperty("id", out var subnetId))
            {
                var subnetIdStr = subnetId.GetString() ?? "";
                // Subnets live inside VNet properties rather than as standalone graph nodes;
                // preserve the subnet ID as an unresolved relationship target.
                if (!string.IsNullOrEmpty(subnetIdStr))
                    graph.AddEdge(nic.ResourceId, subnetIdStr, "subnet");
            }

            // Also record the associated public IP if present
            if (cfgProps.TryGetProperty("publicIPAddress", out var pip)
                && pip.TryGetProperty("id", out var pipId))
            {
                var pipIdStr = pipId.GetString() ?? "";
                if (!string.IsNullOrEmpty(pipIdStr))
                    graph.AddEdge(nic.ResourceId, pipIdStr, "public ip");
            }
        }
    }

    /// <summary>
    /// Public IP → its owner (e.g. Application Gateway, Load Balancer, NIC) via
    /// <c>properties.ipConfiguration.id</c> which encodes the parent resource ID.
    /// </summary>
    private static void ExtractPublicIpEdges(TenantNode pip, TenantGraph graph)
    {
        var props = pip.Properties;
        if (!props.TryGetProperty("ipConfiguration", out var cfg)) return;
        if (!cfg.TryGetProperty("id", out var cfgId)) return;

        var cfgIdStr = cfgId.GetString() ?? "";
        if (string.IsNullOrEmpty(cfgIdStr)) return;

        // The ipConfiguration ID is something like:
        // /subscriptions/{s}/resourceGroups/{rg}/providers/{type}/{name}/frontendIPConfigurations/{fname}
        // Strip the last two path segments to get the parent resource ID.
        var parentId = StripChildSegments(cfgIdStr, 2);
        if (!string.IsNullOrEmpty(parentId))
            graph.AddEdge(parentId, pip.ResourceId, "public ip");
    }

    /// <summary>
    /// Application Gateway → Public IPs (frontend) and → backend pool members.
    /// Backends that cannot be resolved to a known resource ID are skipped
    /// (no new queries issued).
    /// </summary>
    private static void ExtractAppGatewayEdges(TenantNode agw, TenantGraph graph)
    {
        var props = agw.Properties;

        // Inbound: frontendIPConfigurations → publicIPAddress
        if (props.TryGetProperty("frontendIPConfigurations", out var feConfs)
            && feConfs.ValueKind == JsonValueKind.Array)
        {
            foreach (var fe in feConfs.EnumerateArray())
            {
                if (fe.TryGetProperty("properties", out var feProps)
                    && feProps.TryGetProperty("publicIPAddress", out var pip)
                    && pip.TryGetProperty("id", out var pipId))
                {
                    var pipIdStr = pipId.GetString() ?? "";
                    if (!string.IsNullOrEmpty(pipIdStr))
                        graph.AddEdge(agw.ResourceId, pipIdStr, "frontend ip");
                }
            }
        }

        // WAF policy (firewallPolicy reference)
        if (props.TryGetProperty("firewallPolicy", out var fwPol)
            && fwPol.ValueKind != JsonValueKind.Undefined
            && fwPol.TryGetProperty("id", out var fwId))
        {
            var fwIdStr = fwId.GetString() ?? "";
            if (!string.IsNullOrEmpty(fwIdStr))
                graph.AddEdge(agw.ResourceId, fwIdStr, "waf policy");
        }

        // Outbound: backendAddressPools → each backend address
        if (!props.TryGetProperty("backendAddressPools", out var bePools)
            || bePools.ValueKind != JsonValueKind.Array) return;

        foreach (var pool in bePools.EnumerateArray())
        {
            if (!pool.TryGetProperty("properties", out var poolProps)) continue;
            if (!poolProps.TryGetProperty("backendAddresses", out var addresses)
                || addresses.ValueKind != JsonValueKind.Array) continue;

            foreach (var addr in addresses.EnumerateArray())
            {
                ResolveBackendAddress(agw.ResourceId, addr, "backend pool", graph);
            }
        }
    }

    /// <summary>Classic Front Door → backend pools.</summary>
    private static void ExtractFrontDoorEdges(TenantNode fd, TenantGraph graph)
    {
        var props = fd.Properties;

        // Classic FD: backendPools[*].properties.backends[*].address
        if (!props.TryGetProperty("backendPools", out var pools)
            || pools.ValueKind != JsonValueKind.Array) return;

        foreach (var pool in pools.EnumerateArray())
        {
            if (!pool.TryGetProperty("properties", out var poolProps)) continue;
            if (!poolProps.TryGetProperty("backends", out var backends)
                || backends.ValueKind != JsonValueKind.Array) continue;

            foreach (var backend in backends.EnumerateArray())
            {
                if (backend.TryGetProperty("address", out var addrEl))
                {
                    var addr = addrEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(addr))
                    {
                        var target = graph.FindByFqdn(addr) ?? graph.FindByIpAddress(addr);
                        if (target is not null)
                            graph.AddEdge(fd.ResourceId, target.ResourceId, "backend");
                    }
                }
            }
        }
    }

    /// <summary>AFD Standard/Premium (microsoft.cdn/profiles) → origin groups if embedded.</summary>
    private static void ExtractAfdEdges(TenantNode afd, TenantGraph graph)
    {
        // AFD Standard/Premium stores origin/route details as child resources
        // (afdendpoints, origingroups, origins) which appear as separate TenantNodes
        // with their own resource IDs in the graph.
        // Link child resources back to their parent profile via ID prefix matching.
        var prefix = afd.ResourceId.TrimEnd('/') + "/";
        foreach (var node in graph.Nodes.Values)
        {
            if (node.ResourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && node.ResourceId != afd.ResourceId)
            {
                graph.AddEdge(afd.ResourceId, node.ResourceId, "child resource");
            }
        }
    }

    /// <summary>VM → NICs (via properties.networkProfile.networkInterfaces).</summary>
    private static void ExtractVmEdges(TenantNode vm, TenantGraph graph)
    {
        var props = vm.Properties;
        if (!props.TryGetProperty("networkProfile", out var np)) return;
        if (!np.TryGetProperty("networkInterfaces", out var nics)
            || nics.ValueKind != JsonValueKind.Array) return;

        foreach (var nic in nics.EnumerateArray())
        {
            if (nic.TryGetProperty("id", out var nicId))
            {
                var nicIdStr = nicId.GetString() ?? "";
                if (!string.IsNullOrEmpty(nicIdStr))
                    graph.AddEdge(vm.ResourceId, nicIdStr, "network interface");
            }
        }
    }

    /// <summary>VMSS → subnet (properties.virtualMachineProfile.networkProfile).</summary>
    private static void ExtractVmssEdges(TenantNode vmss, TenantGraph graph)
    {
        var props = vmss.Properties;
        if (!props.TryGetProperty("virtualMachineProfile", out var vmp)) return;
        if (!vmp.TryGetProperty("networkProfile", out var np)) return;
        if (!np.TryGetProperty("networkInterfaceConfigurations", out var nics)
            || nics.ValueKind != JsonValueKind.Array) return;

        foreach (var nic in nics.EnumerateArray())
        {
            if (!nic.TryGetProperty("properties", out var nicProps)) continue;
            if (!nicProps.TryGetProperty("ipConfigurations", out var cfgs)
                || cfgs.ValueKind != JsonValueKind.Array) continue;

            foreach (var cfg in cfgs.EnumerateArray())
            {
                if (!cfg.TryGetProperty("properties", out var cfgProps)) continue;
                if (cfgProps.TryGetProperty("subnet", out var subnet)
                    && subnet.TryGetProperty("id", out var subnetId))
                {
                    var subnetIdStr = subnetId.GetString() ?? "";
                    if (!string.IsNullOrEmpty(subnetIdStr))
                        graph.AddEdge(vmss.ResourceId, subnetIdStr, "subnet");
                }
            }
        }
    }

    /// <summary>AKS → subnets (agentPoolProfiles[*].subnetId).</summary>
    private static void ExtractAksEdges(TenantNode aks, TenantGraph graph)
    {
        var props = aks.Properties;
        if (!props.TryGetProperty("agentPoolProfiles", out var pools)
            || pools.ValueKind != JsonValueKind.Array) return;

        foreach (var pool in pools.EnumerateArray())
        {
            if (pool.TryGetProperty("subnetId", out var subnetId))
            {
                var subnetIdStr = subnetId.GetString() ?? "";
                if (!string.IsNullOrEmpty(subnetIdStr))
                    graph.AddEdge(aks.ResourceId, subnetIdStr, "node pool subnet");
            }
        }
    }

    /// <summary>Container App → subnet and → ACR repo via container image string.</summary>
    private static void ExtractContainerAppEdges(TenantNode app, TenantGraph graph)
    {
        var props = app.Properties;

        // Subnet via managedEnvironmentId → environment → subnet
        // (the environment is a separate TenantNode; just link to it)
        if (props.TryGetProperty("managedEnvironmentId", out var envId))
        {
            var envIdStr = envId.GetString() ?? "";
            if (!string.IsNullOrEmpty(envIdStr))
                graph.AddEdge(app.ResourceId, envIdStr, "managed environment");
        }

        // Container images
        if (props.TryGetProperty("template", out var tmpl)
            && tmpl.TryGetProperty("containers", out var containers)
            && containers.ValueKind == JsonValueKind.Array)
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (container.TryGetProperty("image", out var imgEl))
                    ResolveContainerImage(app.ResourceId, imgEl.GetString(), graph);
            }
        }
    }

    /// <summary>Web App / Function App → App Service Plan, subnet, ACR repo.</summary>
    private static void ExtractWebAppEdges(TenantNode site, TenantGraph graph)
    {
        var props = site.Properties;

        // App Service Plan (serverFarmId)
        if (props.TryGetProperty("serverFarmId", out var sfId))
        {
            var sfIdStr = sfId.ValueKind == JsonValueKind.String ? sfId.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(sfIdStr))
                graph.AddEdge(site.ResourceId, sfIdStr, "app service plan");
        }

        // Subnet via virtualNetworkSubnetId
        if (props.TryGetProperty("virtualNetworkSubnetId", out var subnetId))
        {
            var subnetIdStr = subnetId.ValueKind == JsonValueKind.String ? subnetId.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(subnetIdStr))
                graph.AddEdge(site.ResourceId, subnetIdStr, "vnet integration subnet");
        }

        if (!props.TryGetProperty("siteConfig", out var cfg)
            || cfg.ValueKind == JsonValueKind.Undefined) return;

        foreach (var key in new[] { "linuxFxVersion", "windowsFxVersion" })
        {
            if (cfg.TryGetProperty(key, out var fxVer))
            {
                var raw = fxVer.GetString() ?? "";
                // Format: DOCKER|registry/repo:tag  or  registry/repo:tag
                var image = raw.StartsWith("DOCKER|", StringComparison.OrdinalIgnoreCase)
                    ? raw["DOCKER|".Length..]
                    : raw;
                ResolveContainerImage(site.ResourceId, image, graph);
            }
        }
    }

    /// <summary>Private Endpoint → its target service via privateLinkServiceConnections.</summary>
    private static void ExtractPrivateEndpointEdges(TenantNode pe, TenantGraph graph)
    {
        var props = pe.Properties;

        // Subnet
        if (props.TryGetProperty("subnet", out var subnet)
            && subnet.TryGetProperty("id", out var subnetId))
        {
            var subnetIdStr = subnetId.GetString() ?? "";
            if (!string.IsNullOrEmpty(subnetIdStr))
                graph.AddEdge(pe.ResourceId, subnetIdStr, "subnet");
        }

        // Target service
        if (!props.TryGetProperty("privateLinkServiceConnections", out var conns)
            || conns.ValueKind != JsonValueKind.Array) return;

        foreach (var conn in conns.EnumerateArray())
        {
            if (!conn.TryGetProperty("properties", out var connProps)) continue;
            if (!connProps.TryGetProperty("privateLinkServiceId", out var svcId)) continue;

            var svcIdStr = svcId.GetString() ?? "";
            if (!string.IsNullOrEmpty(svcIdStr))
                graph.AddEdge(pe.ResourceId, svcIdStr, "private link");
        }
    }

    /// <summary>
    /// Load Balancer → Public IPs (frontendIPConfigurations) and → backend NICs/VMs.
    /// </summary>
    private static void ExtractLoadBalancerEdges(TenantNode lb, TenantGraph graph)
    {
        var props = lb.Properties;

        // Frontend IP → Public IP
        if (props.TryGetProperty("frontendIPConfigurations", out var feConfs)
            && feConfs.ValueKind == JsonValueKind.Array)
        {
            foreach (var fe in feConfs.EnumerateArray())
            {
                if (!fe.TryGetProperty("properties", out var feProps)) continue;
                if (feProps.TryGetProperty("publicIPAddress", out var pip)
                    && pip.TryGetProperty("id", out var pipId))
                {
                    var pipIdStr = pipId.GetString() ?? "";
                    if (!string.IsNullOrEmpty(pipIdStr))
                        graph.AddEdge(lb.ResourceId, pipIdStr, "frontend ip");
                }
            }
        }

        // Backend pool → NICs (backendAddressPools[*].properties.backendIPConfigurations are child NICs)
        if (props.TryGetProperty("backendAddressPools", out var bePools)
            && bePools.ValueKind == JsonValueKind.Array)
        {
            foreach (var pool in bePools.EnumerateArray())
            {
                if (!pool.TryGetProperty("properties", out var poolProps)) continue;
                if (!poolProps.TryGetProperty("backendIPConfigurations", out var cfgs)
                    || cfgs.ValueKind != JsonValueKind.Array) continue;

                foreach (var cfg in cfgs.EnumerateArray())
                {
                    if (cfg.TryGetProperty("id", out var nicCfgId))
                    {
                        // NIC config ID → parent NIC ID (strip last 2 segments)
                        var parentNicId = StripChildSegments(nicCfgId.GetString() ?? "", 2);
                        if (!string.IsNullOrEmpty(parentNicId))
                            graph.AddEdge(lb.ResourceId, parentNicId, "backend pool");
                    }
                }
            }
        }
    }

    /// <summary>DNS record set (A/CNAME) → target IP or FQDN resolved against in-memory map.</summary>
    private static void ExtractDnsRecordEdges(TenantNode record, TenantGraph graph)
    {
        // Parent zone is the grandparent of the record (strip last 2 path segments: type/name)
        var zoneId = StripChildSegments(record.ResourceId, 2);
        if (!string.IsNullOrEmpty(zoneId))
            graph.AddEdge(zoneId, record.ResourceId, "dns record");

        var props = record.Properties;

        // A records → resolve IP to known resource
        if (props.TryGetProperty("ARecords", out var aRecords)
            && aRecords.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in aRecords.EnumerateArray())
            {
                if (a.TryGetProperty("ipv4Address", out var ipEl))
                {
                    var ip     = ipEl.GetString() ?? "";
                    var target = graph.FindByIpAddress(ip);
                    if (target is not null)
                        graph.AddEdge(record.ResourceId, target.ResourceId, "a record");
                }
            }
        }

        // CNAME → resolve FQDN
        if (props.TryGetProperty("CNAMERecord", out var cname)
            && cname.TryGetProperty("cname", out var cnameEl))
        {
            var fqdn   = cnameEl.GetString() ?? "";
            var target = graph.FindByFqdn(fqdn);
            if (target is not null)
                graph.AddEdge(record.ResourceId, target.ResourceId, "cname");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a backend address JSON element (with "fqdn" or "ipAddress" property)
    /// to a node in the graph and adds an edge.  No new queries — lookup is pure in-memory.
    /// </summary>
    private static void ResolveBackendAddress(
        string       sourceId,
        JsonElement  addrEl,
        string       label,
        TenantGraph  graph)
    {
        TenantNode? target = null;

        if (addrEl.TryGetProperty("fqdn", out var fqdnEl))
        {
            var fqdn = fqdnEl.GetString() ?? "";
            if (!string.IsNullOrEmpty(fqdn))
                target = graph.FindByFqdn(fqdn);
        }

        if (target is null && addrEl.TryGetProperty("ipAddress", out var ipEl))
        {
            var ip = ipEl.GetString() ?? "";
            if (!string.IsNullOrEmpty(ip))
                target = graph.FindByIpAddress(ip);
        }

        if (target is not null)
            graph.AddEdge(sourceId, target.ResourceId, label);
        // Unresolvable addresses are intentionally dropped — no new query.
    }

    /// <summary>
    /// Parses a container image reference (<c>registry.azurecr.io/repo:tag</c>)
    /// and adds an edge from <paramref name="sourceId"/> to the matching ACR repo
    /// node if it exists in the graph.
    /// </summary>
    private static void ResolveContainerImage(string sourceId, string? imageRef, TenantGraph graph)
    {
        if (string.IsNullOrEmpty(imageRef)) return;

        // Strip tag/digest
        var noTag = imageRef.Contains('@')
            ? imageRef[..imageRef.LastIndexOf('@')]
            : imageRef.Contains(':')
                ? imageRef[..imageRef.LastIndexOf(':')]
                : imageRef;

        // Split "registry.azurecr.io/repo/name" → registry host + repo path
        var slashIdx = noTag.IndexOf('/');
        if (slashIdx < 0) return;

        var registryHost = noTag[..slashIdx];
        var repoPath     = noTag[(slashIdx + 1)..];

        // Only handle ACR registries (*.azurecr.io or private CNAME)
        // Match by looking for a registry whose name appears in the host.
        foreach (var node in graph.Nodes.Values)
        {
            if (node.Type != "microsoft.containerregistry/registries") continue;
            if (!registryHost.StartsWith(node.Name, StringComparison.OrdinalIgnoreCase)) continue;

            // Look for a repo node with matching name (created by TenantEnumerator)
            var repoNodeId = $"{node.ResourceId}/repositories/{repoPath}";
            if (graph.HasNode(repoNodeId))
            {
                graph.AddEdge(sourceId, repoNodeId, "container image");
                return;
            }

            // Repo not found but registry is known — link to registry
            graph.AddEdge(sourceId, node.ResourceId, "container image");
            return;
        }
    }

    /// <summary>
    /// Strips the last <paramref name="count"/> path segments from an ARM resource ID.
    /// Used to navigate from a child resource ID to its parent.
    /// </summary>
    private static string StripChildSegments(string resourceId, int count) =>
        ArmId.StripSegments(resourceId, count);

    /// <summary>
    /// Well-known Azure built-in role definition GUIDs → display names.
    /// Only the most operationally significant roles are mapped; others show as "Role:{guid}".
    /// </summary>
    private static readonly Dictionary<string, string> s_roleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8e3af657-a8ff-443c-a75c-2fe8c4bcb635"] = "Owner",
        ["b24988ac-6180-42a0-ab88-20f7382dd24c"] = "Contributor",
        ["acdd72a7-3385-48ef-bd42-f606fba81ae7"] = "Reader",
        ["18d7d88d-d35e-4fb5-a5c3-7773c20a72d9"] = "User Access Administrator",
        ["f58310d9-a9f6-439a-9e8d-f62e7b41a168"] = "Role Based Access Control Administrator",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
        ["cf1c38e5-3621-4004-a7cb-879624dced7c"] = "Application Developer",
        ["5e0bd9bd-7b93-4f28-af87-19fc36ad61bd"] = "Azure AD Joined Device Local Administrator",
        ["00482a5a-887f-4fb3-b363-3b7fe8e74483"] = "Global Administrator",
        ["e8311da5-1d17-4245-9c67-e4301ef9d41a"] = "Storage Blob Data Contributor",
        ["2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"] = "Storage Blob Data Reader",
        ["ba92f5b4-2d11-453d-a403-e96b0029c9fe"] = "Storage Blob Data Owner",
        ["4633458b-17de-408a-b874-0445c86b69e6"] = "Key Vault Secrets User",
        ["b86a8fe4-44ce-4948-aee5-eccb2c155cd7"] = "Key Vault Secrets Officer",
        ["14b46e9e-c2b0-4a44-a0c5-ca13b63a81ad"] = "Key Vault Crypto Officer",
        ["12338af0-0e69-4776-bea7-57ae8d297424"] = "Key Vault Crypto User",
        ["21090545-7ca7-4776-b22c-e363652d74d2"] = "Key Vault Reader",
        ["a4417e6f-fecd-4de8-b567-7b0420556985"] = "Key Vault Certificate Officer",
        ["7f951dda-4ed3-4680-a7ca-43fe172d538d"] = "AcrPull",
        ["8311e382-0749-4cb8-b61a-304f252e45ec"] = "AcrPush",
        ["9f38b2e2-1f6e-4f79-b0f5-2264b50a4c37"] = "SQL Server Contributor",
    };

    /// <summary>
    /// Role assignment → scoped resource edge.
    /// Edge direction: assignment node → scoped resource (label = "role:{RoleName}").
    /// This means the scoped resource receives an INBOUND edge from the assignment,
    /// which VaultWriter renders as "has role assignment" on the resource file.
    /// </summary>
    internal static void ExtractRoleAssignmentEdges(TenantNode assignment, TenantGraph graph)
    {
        var props = assignment.Properties;
        if (props.ValueKind != JsonValueKind.Object) return;

        var roleName = GetRoleName(props);

        // Scope: can be subscription, resource group, or specific resource
        if (!props.TryGetProperty("scope", out var scopeEl)
            || scopeEl.ValueKind != JsonValueKind.String) return;

        var scope = scopeEl.GetString() ?? "";
        if (string.IsNullOrEmpty(scope)) return;

        // Only link to nodes that exist in the graph (exact resource ID match).
        // Skip subscription-level (/subscriptions/{id}) and RG-level scopes
        // to avoid attaching every subscription-wide Owner to every resource.
        if (graph.HasNode(scope))
            graph.AddEdge(assignment.ResourceId, scope, $"role:{roleName}");
    }

    /// <summary>
    /// Resolves the display role name from a role assignment's properties bag
    /// (well-known GUID map, otherwise "Role:{guid-prefix}").
    /// </summary>
    internal static string GetRoleName(JsonElement props)
    {
        if (props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("roleDefinitionId", out var rdId)
            && rdId.ValueKind == JsonValueKind.String)
        {
            var rdIdStr = rdId.GetString() ?? "";
            // Last segment of the role definition ID is the GUID
            var guid = rdIdStr.Contains('/') ? rdIdStr[(rdIdStr.LastIndexOf('/') + 1)..] : rdIdStr;
            return s_roleNames.TryGetValue(guid, out var name) ? name : $"Role:{guid[..Math.Min(8, guid.Length)]}";
        }
        return "Role";
    }
}
