using System.Text.Json;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Vault;
using YamlDotNet.RepresentationModel;

namespace AzToMarkdown.Tests;

/// <summary>
/// Consumer validation (see docs/metadata-reference.md): every property path
/// <see cref="AzToMarkdown.Core.Azure.RelationshipExtractor"/> reads must be retrievable — value-identical — from the
/// schema-v1 YAML front-matter alone. Each fixture mirrors the ARG payload shape for a resource
/// type <see cref="AzToMarkdown.Core.Azure.RelationshipExtractor"/> has a dedicated edge-extraction case for.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ConsumerPathValidationTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Fixtures: resource type → (properties JSON, consumed property paths)
    // Paths use dot notation with [n] array indexers.
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, (string Json, string[] Paths)> _fixtures = new()
    {
        ["microsoft.network/publicipaddresses"] = ("""
            { "ipAddress": "20.100.1.2",
              "ipConfiguration": { "id": "/subscriptions/x/loadBalancers/lb/frontendIPConfigurations/fe1" },
              "dnsSettings": { "fqdn": "myapp.norwayeast.cloudapp.azure.com", "domainNameLabel": "myapp" } }
            """,
            ["ipAddress", "ipConfiguration.id", "dnsSettings.fqdn"]),

        ["microsoft.network/loadbalancers"] = ("""
            { "frontendIPConfigurations": [ { "name": "fe1", "properties": { "privateIPAddress": "10.0.0.10" } } ],
              "backendAddressPools": [ { "name": "bep1", "properties": { "backendIPConfigurations": [ { "id": "/subscriptions/x/nic1/ipConfigurations/ip1" } ] } } ],
              "loadBalancingRules": [ { "name": "https", "properties": { "protocol": "Tcp", "frontendPort": 443, "backendPort": 443 } } ] }
            """,
            ["frontendIPConfigurations[0].properties.privateIPAddress",
             "backendAddressPools[0].properties.backendIPConfigurations[0].id",
             "loadBalancingRules[0].name",
             "loadBalancingRules[0].properties.protocol",
             "loadBalancingRules[0].properties.frontendPort",
             "loadBalancingRules[0].properties.backendPort"]),

        ["microsoft.network/networkinterfaces"] = ("""
            { "ipConfigurations": [ { "name": "ipconfig1", "properties": {
                  "privateIPAddress": "10.0.0.4",
                  "subnet": { "id": "/subscriptions/x/virtualNetworks/vnet/subnets/snet" },
                  "publicIPAddress": { "id": "/subscriptions/x/publicIPAddresses/pip1" } } } ],
              "virtualMachine": { "id": "/subscriptions/x/virtualMachines/vm1" } }
            """,
            ["ipConfigurations[0].properties.privateIPAddress",
             "ipConfigurations[0].properties.subnet.id",
             "ipConfigurations[0].properties.publicIPAddress.id",
             "virtualMachine.id"]),

        ["microsoft.network/applicationgateways"] = ("""
            { "frontendIPConfigurations": [ { "name": "fe", "properties": { "publicIPAddress": { "id": "/subscriptions/x/publicIPAddresses/agw-pip" } } } ],
              "frontendPorts": [ { "name": "port_443", "properties": { "port": 443 } } ],
              "backendAddressPools": [ { "id": "/x/agw/backendAddressPools/pool1", "name": "pool1",
                  "properties": { "backendAddresses": [ { "ipAddress": "10.0.1.4", "fqdn": "backend.example.com" } ],
                                   "backendIPConfigurations": [ { "id": "/subscriptions/x/virtualMachineScaleSets/vmss1/virtualMachines/0/networkInterfaces/nic/ipConfigurations/ip" } ] } } ],
              "backendHttpSettingsCollection": [ { "id": "/x/agw/backendHttpSettingsCollection/http1", "name": "http1", "properties": { "protocol": "Https", "port": 443 } } ],
              "httpListeners": [ { "id": "/x/agw/httpListeners/l1", "name": "l1",
                  "properties": { "protocol": "Https", "hostName": "www.example.com", "requireServerNameIndication": true, "frontendPort": { "id": "/x/agw/frontendPorts/port_443" } } } ],
              "urlPathMaps": [ { "id": "/x/agw/urlPathMaps/map1", "properties": {
                  "defaultBackendAddressPool": { "id": "/x/agw/backendAddressPools/pool1" },
                  "defaultBackendHttpSettings": { "id": "/x/agw/backendHttpSettingsCollection/http1" },
                  "pathRules": [ { "name": "api", "properties": { "paths": ["/api/*"],
                      "backendAddressPool": { "id": "/x/agw/backendAddressPools/pool1" },
                      "backendHttpSettings": { "id": "/x/agw/backendHttpSettingsCollection/http1" } } } ] } } ],
              "requestRoutingRules": [ { "properties": { "httpListener": { "id": "/x/agw/httpListeners/l1" },
                  "ruleType": "PathBasedRouting", "urlPathMap": { "id": "/x/agw/urlPathMaps/map1" },
                  "backendAddressPool": { "id": "/x/agw/backendAddressPools/pool1" },
                  "backendHttpSettings": { "id": "/x/agw/backendHttpSettingsCollection/http1" } } } ],
              "firewallPolicy": { "id": "/subscriptions/x/ApplicationGatewayWebApplicationFirewallPolicies/waf1" } }
            """,
            ["frontendIPConfigurations[0].properties.publicIPAddress.id",
             "frontendPorts[0].name", "frontendPorts[0].properties.port",
             "backendAddressPools[0].properties.backendAddresses[0].ipAddress",
             "backendAddressPools[0].properties.backendAddresses[0].fqdn",
             "backendAddressPools[0].properties.backendIPConfigurations[0].id",
             "backendHttpSettingsCollection[0].properties.protocol",
             "backendHttpSettingsCollection[0].properties.port",
             "httpListeners[0].properties.protocol",
             "httpListeners[0].properties.hostName",
             "httpListeners[0].properties.requireServerNameIndication",
             "httpListeners[0].properties.frontendPort.id",
             "urlPathMaps[0].properties.defaultBackendAddressPool.id",
             "urlPathMaps[0].properties.pathRules[0].properties.paths[0]",
             "urlPathMaps[0].properties.pathRules[0].properties.backendAddressPool.id",
             "requestRoutingRules[0].properties.httpListener.id",
             "requestRoutingRules[0].properties.ruleType",
             "requestRoutingRules[0].properties.urlPathMap.id",
             "firewallPolicy.id"]),

        ["microsoft.network/frontdoors"] = ("""
            { "frontendEndpoints": [ { "id": "/x/fd/frontendEndpoints/fe1", "properties": { "hostName": "myfd.azurefd.net" } } ],
              "backendPools": [ { "id": "/x/fd/backendPools/bp1", "properties": { "backends": [ { "address": "origin.example.com" } ] } } ],
              "routingRules": [ { "name": "rule1", "properties": {
                  "frontendEndpoints": [ { "id": "/x/fd/frontendEndpoints/fe1" } ],
                  "patternsToMatch": ["/*"],
                  "routeConfiguration": { "@odata.type": "#Microsoft.Azure.FrontDoor.Models.FrontdoorForwardingConfiguration",
                                           "backendPool": { "id": "/x/fd/backendPools/bp1" } } } } ] }
            """,
            ["frontendEndpoints[0].properties.hostName",
             "backendPools[0].properties.backends[0].address",
             "routingRules[0].properties.frontendEndpoints[0].id",
             "routingRules[0].properties.patternsToMatch[0]",
             "routingRules[0].properties.routeConfiguration.@odata.type",
             "routingRules[0].properties.routeConfiguration.backendPool.id"]),

        ["microsoft.web/sites"] = ("""
            { "defaultHostName": "myapp.azurewebsites.net",
              "hostNames": ["myapp.azurewebsites.net", "www.example.com"],
              "customDomains": [],
              "serverFarmId": "/subscriptions/x/serverfarms/plan1",
              "virtualNetworkSubnetId": "/subscriptions/x/virtualNetworks/vnet/subnets/snet-integration",
              "reserved": true,
              "siteConfig": { "linuxFxVersion": "DOTNETCORE|8.0", "windowsFxVersion": null,
                               "netFrameworkVersion": "v4.0", "javaVersion": null, "phpVersion": null,
                               "nodeVersion": null, "pythonVersion": null, "currentStack": null } }
            """,
            ["defaultHostName", "hostNames[0]", "serverFarmId", "virtualNetworkSubnetId", "reserved",
             "siteConfig.linuxFxVersion", "siteConfig.netFrameworkVersion"]),

        ["microsoft.containerservice/managedclusters"] = ("""
            { "nodeResourceGroup": "MC_rg_cluster_norwayeast", "kubernetesVersion": "1.31.3",
              "agentPoolProfiles": [ { "name": "np1", "subnetId": "/subscriptions/x/virtualNetworks/vnet/subnets/snet-aks" } ] }
            """,
            ["nodeResourceGroup", "kubernetesVersion", "agentPoolProfiles[0].subnetId"]),

        ["microsoft.compute/virtualmachines"] = ("""
            { "networkProfile": { "networkInterfaces": [ { "id": "/subscriptions/x/networkInterfaces/nic1" } ] },
              "storageProfile": { "osDisk": { "osType": "Linux" } } }
            """,
            ["networkProfile.networkInterfaces[0].id", "storageProfile.osDisk.osType"]),

        ["microsoft.compute/virtualmachinescalesets"] = ("""
            { "virtualMachineProfile": {
                "networkProfile": { "networkInterfaceConfigurations": [ { "properties": { "ipConfigurations": [ { "properties": { "subnet": { "id": "/subscriptions/x/subnets/snet-sf" } } } ] } } ] },
                "storageProfile": { "osDisk": { "osType": "Windows" } },
                "extensionProfile": { "extensions": [ { "properties": {
                    "type": "ServiceFabricNode",
                    "settings": { "clusterEndpoint": "https://sf.example.com:19080", "nodeTypeRef": "nt1vm", "durabilityLevel": "Bronze" } } } ] } } }
            """,
            ["virtualMachineProfile.networkProfile.networkInterfaceConfigurations[0].properties.ipConfigurations[0].properties.subnet.id",
             "virtualMachineProfile.extensionProfile.extensions[0].properties.type",
             "virtualMachineProfile.extensionProfile.extensions[0].properties.settings.clusterEndpoint",
             "virtualMachineProfile.extensionProfile.extensions[0].properties.settings.nodeTypeRef",
             "virtualMachineProfile.extensionProfile.extensions[0].properties.settings.durabilityLevel"]),

        ["microsoft.network/dnszones/a"] = ("""
            { "ARecords": [ { "ipv4Address": "20.100.1.2" } ], "TTL": 3600 }
            """,
            ["ARecords[0].ipv4Address"]),

        ["microsoft.network/dnszones/cname"] = ("""
            { "CNAMERecord": { "cname": "myapp.azurewebsites.net" }, "TTL": 3600 }
            """,
            ["CNAMERecord.cname"]),

        ["microsoft.network/privateendpoints"] = ("""
            { "subnet": { "id": "/subscriptions/x/virtualNetworks/vnet/subnets/snet-pe" },
              "privateLinkServiceConnections": [ { "properties": { "privateLinkServiceId": "/subscriptions/x/storageAccounts/st1" } } ] }
            """,
            ["subnet.id", "privateLinkServiceConnections[0].properties.privateLinkServiceId"]),

        ["microsoft.app/containerapps"] = ("""
            { "managedEnvironmentId": "/subscriptions/x/managedEnvironments/env1",
              "template": { "containers": [ { "image": "myacr.azurecr.io/app:v1.2.3" } ] } }
            """,
            ["managedEnvironmentId", "template.containers[0].image"]),

        ["microsoft.authorization/roleassignments"] = ("""
            { "roleDefinitionId": "/subscriptions/x/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
              "scope": "/subscriptions/x/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv1",
              "principalId": "11111111-2222-3333-4444-555555555555" }
            """,
            ["roleDefinitionId", "scope", "principalId"]),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // The test
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void EveryConsumedPropertyPath_IsRetrievableFromYaml_ValueIdentical()
    {
        var failures = new List<string>();

        foreach (var (type, (json, paths)) in _fixtures)
        {
            using var original = JsonDocument.Parse(json);
            var node = new TenantNode
            {
                ResourceId     = $"/subscriptions/x/resourceGroups/rg/providers/{type}/fixture",
                Name           = "fixture",
                Type           = type,
                SubscriptionId = "x",
                ResourceGroup  = "rg",
                Location       = "norwayeast",
                Properties     = original.RootElement.Clone(),
            };

            // Serialize → reparse the YAML → back to JSON
            var serializer = new FrontMatterSerializer("0.0.0-test");
            var fm         = serializer.Serialize(new FrontMatterContext(node, "sub", [], [], null, []));
            var yamlText   = fm[4..fm.IndexOf("\n---\n", 4, StringComparison.Ordinal)];
            var root       = YamlJsonConverter.ParseDocument(yamlText)!;
            var meta       = (YamlMappingNode)root.Children[new YamlScalarNode("azure_metadata")];
            var propsJson  = YamlJsonConverter.ToJson(meta.Children[new YamlScalarNode("properties")]);
            if (propsJson is null) { failures.Add($"{type}: properties came back null"); continue; }
            var reparsed   = JsonDocument.Parse(propsJson.ToJsonString()).RootElement;

            // Full lossless parity for the whole bag…
            if (!YamlJsonConverter.JsonDeepEquals(original.RootElement, reparsed, out var diff))
                failures.Add($"{type}: full-bag parity failed at {diff}");

            // …and each consumed path exists (guards against fixture/path typos going stale).
            foreach (var path in paths)
            {
                if (!TryNavigate(reparsed, path, out var reparsedValue))
                { failures.Add($"{type}: path '{path}' missing from reparsed YAML"); continue; }
                if (!TryNavigate(original.RootElement, path, out var originalValue))
                { failures.Add($"{type}: path '{path}' missing from ORIGINAL fixture (fix the fixture)"); continue; }
                if (!YamlJsonConverter.JsonDeepEquals(originalValue, reparsedValue, out var pathDiff))
                    failures.Add($"{type}: value mismatch for '{path}' at {pathDiff}");
            }
        }

        Assert.AreEqual(0, failures.Count,
            $"{failures.Count} consumer-path validation failure(s):\n" + string.Join("\n", failures));
    }

    /// <summary>Navigates "a.b[0].c" style paths. Property names may contain dots when they don't match a direct child ("@odata.type").</summary>
    private static bool TryNavigate(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                var index = int.Parse(path[(i + 1)..close]);
                if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength()) return false;
                value = value[index];
                i = close + 1;
                if (i < path.Length && path[i] == '.') i++;
            }
            else
            {
                var end = NextSeparator(path, i);
                var name = path[i..end];
                // Greedy fallback: if the dotted prefix isn't a child, try extending across dots
                // (handles keys like "@odata.type").
                while (value.ValueKind == JsonValueKind.Object && !value.TryGetProperty(name, out _)
                       && end < path.Length && path[end] == '.')
                {
                    end = NextSeparator(path, end + 1);
                    name = path[i..end];
                }
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var next)) return false;
                value = next;
                i = end;
                if (i < path.Length && path[i] == '.') i++;
            }
        }
        return true;
    }

    private static int NextSeparator(string path, int from)
    {
        for (var j = from; j < path.Length; j++)
            if (path[j] is '.' or '[') return j;
        return path.Length;
    }
}
