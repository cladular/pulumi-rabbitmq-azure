using System.Collections.Generic;
using System.Linq;
using Pulumi;
using Pulumi.Azure.ContainerService;
using Pulumi.Azure.ContainerService.Inputs;
using Pulumi.Azure.Core;
using Pulumi.Azure.Network;
using Pulumi.Azure.Network.Inputs;
using Pulumi.Azure.PrivateDns;
using Pulumi.Azure.Storage;
using Pulumi.Random;

class ExampleStack : Stack
{
    private const string DeploymentName = "rabbitmq";
    private const string RabbitNodeName = "rabbit";
    private const string ZoneName = "example.com";
    private const string ReverseZoneName = "10.in-addr.arpa";
    private const string DiscoveryRecordName = "discovery";
    private const string ConfigShareName = "config";
    private const string MnesiaShareName = "mnesia";
    private const string SchemaShareName = "schema";
    private const int ContainerStartupDelay = 60;

    private readonly string[] locations = { "centralus", "eastus", "westus" };

    public ExampleStack()
    {
        Stack();
    }

    [Output]
    public Output<string> Cookie { get; private set; } = Output.Create(string.Empty);

    private void Stack()
    {
        var vnetRangePrefix = 1;
        VirtualNetwork[] exNetworks = new VirtualNetwork[locations.Count()];
        Subnet[] exSubnets = new Subnet[locations.Count()];
        Output<string>[] ipAddresses = new Output<string>[locations.Count()];
        Firewall[] firewalls = new Firewall[locations.Count()];

        var commonResourceGroup = ResourceGroup($"rg-{DeploymentName}-common-eastus2", "eastus2");
        var privateDns = PrivateDns(commonResourceGroup.Name);
        var privateReverseDns = PrivateReverseDns(commonResourceGroup.Name);
        var rabbitCookie = RabbitCookie();

        foreach (string location in locations)
        {
            var resourceGroup = ResourceGroup($"rg-{DeploymentName}-{location}", location);
            var inVNet = VNet(resourceGroup, vnetRangePrefix, false);
            var inSNet = InSNet(resourceGroup, vnetRangePrefix, inVNet);
            var networkProfile = NetworkProfile(resourceGroup, vnetRangePrefix, inSNet);
            var exVNet = VNet(resourceGroup, vnetRangePrefix, true);
            var exSNet = ExSNet(resourceGroup, vnetRangePrefix, exVNet);
            var firewall = Firewall(resourceGroup, exSNet, vnetRangePrefix);
            var storage = Storage(resourceGroup, vnetRangePrefix);
            var container = Container(resourceGroup, storage, networkProfile.Id, vnetRangePrefix, rabbitCookie, new [] { firewall });

            exNetworks[vnetRangePrefix / 2] = exVNet;
            exSubnets[vnetRangePrefix / 2] = exSNet;
            ipAddresses[vnetRangePrefix / 2] = container.IpAddress;
            firewalls[vnetRangePrefix / 2] = firewall;

            RegionPeering(resourceGroup.Name, inVNet, exVNet, vnetRangePrefix);
            RegionRouteTable(resourceGroup, inSNet, vnetRangePrefix, firewall);
            ZoneLink(commonResourceGroup.Name, privateDns, inVNet.Id, vnetRangePrefix, false);
            ZoneLink(commonResourceGroup.Name, privateReverseDns, inVNet.Id, vnetRangePrefix, true);
            ReverseDnsRecord(commonResourceGroup.Name, privateReverseDns, container.IpAddress, vnetRangePrefix);

            vnetRangePrefix += 2;
        }
        
        GlobalPeering(exNetworks);
        GlobalRoutes(exNetworks, exSubnets, firewalls);
        var forwardingRules = IpForwarding(ipAddresses, firewalls);
        var record = DiscoveryDnsRecord(commonResourceGroup.Name, privateDns, ipAddresses);
        var dnsDependencies = forwardingRules
            .Cast<Resource>()
            .Concat(new [] { record })
            .ToArray();

        vnetRangePrefix = 1;

        foreach (Output<string> ipAddress in ipAddresses)
        {
            DnsRecord(commonResourceGroup.Name, privateDns, ipAddress, vnetRangePrefix, dnsDependencies);

            vnetRangePrefix += 2;
        }

        Cookie = rabbitCookie.Result;
    }

    private ResourceGroup ResourceGroup(string name, string location)
    {        
        var resourceGroup = new ResourceGroup(name, new ResourceGroupArgs
        {
            Location = location
        });

        return resourceGroup;
    }

    private Zone PrivateDns(Output<string> resourceGroupName)
    {
        var zone = new Zone(ZoneName, new ZoneArgs
        {
            Name = ZoneName,
            ResourceGroupName = resourceGroupName
        });

        return zone;
    }

    private Zone PrivateReverseDns(Output<string> resourceGroupName)
    {
        var zone = new Zone(ReverseZoneName, new ZoneArgs
        {
            Name = ReverseZoneName,
            ResourceGroupName = resourceGroupName
        });

        return zone;
    }

    private RandomString RabbitCookie()
    {
        var rabbitCookie = new RandomString("rabbit-cookie", new RandomStringArgs
        {
            Length = 20,
            Special = false,
            Lower = false,
            Number = false,
            Upper = true
        });

        return rabbitCookie;
    }

    private void ZoneLink(Output<string> resourceGroupName, Zone privateDns, Output<string> inVNetId, int vnetRangePrefix, bool isReverse)
    {
        var prefix = isReverse ? "rdns" : "dns";
        var zoneLink = new ZoneVirtualNetworkLink($"{prefix}-link-{vnetRangePrefix / 2 + 1}", new ZoneVirtualNetworkLinkArgs
        {
            ResourceGroupName = resourceGroupName,
            PrivateDnsZoneName = privateDns.Name,
            VirtualNetworkId = inVNetId,
            RegistrationEnabled = false
        });
    }

    private Group Container(ResourceGroup resourceGroup, Account storage, Output<string> networkProfileId, int vnetRangePrefix, RandomString rabbitCookie, Resource[] dependencies)
    {
        var containerGroup = new Group($"aci-{DeploymentName}-{vnetRangePrefix / 2 + 1}", new GroupArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            IpAddressType = "Private",
            NetworkProfileId = networkProfileId,
            OsType = "Linux",
            Containers =
            {
                new GroupContainerArgs
                {
                    Name = "rabbitmq",
                    Image = "rabbitmq",
                    Commands = { "/bin/bash", "-c", $"(sleep {ContainerStartupDelay} && docker-entrypoint.sh rabbitmq-server) & wait" },
                    Cpu = 1,
                    Memory = 1.5,
                    Volumes =
                    {
                        ContainerVolume(storage, vnetRangePrefix, ConfigShareName, "/var/lib/rabbitmq/config"),
                        ContainerVolume(storage, vnetRangePrefix, MnesiaShareName, "/var/lib/rabbitmq/mnesia"),
                        ContainerVolume(storage, vnetRangePrefix, SchemaShareName, "/var/lib/rabbitmq/schema"),
                    },
                    Ports =
                    {
                        ContainerPort(15672, "TCP"),
                        ContainerPort(25672, "TCP"),
                        ContainerPort(5672, "TCP"),
                        ContainerPort(4369, "TCP")
                    },
                    EnvironmentVariables =
                    {
                        { "RABBITMQ_ERLANG_COOKIE", rabbitCookie.Result },
                        { "RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", $"-rabbit cluster_formation [{{peer_discovery_backend,rabbit_peer_discovery_dns}},{{peer_discovery_dns,[{{hostname,\"{DiscoveryRecordName}.{ZoneName}\"}}]}}]" },
                        { "RABBITMQ_NODENAME", $"{RabbitNodeName}@{DeploymentName}{vnetRangePrefix / 2 + 1}.{ZoneName}" },
                        { "RABBITMQ_USE_LONGNAME", "true" }
                    }
                }
            }
        },
        new CustomResourceOptions 
        {
            DependsOn = dependencies
        });

        return containerGroup;
    }

    private GroupContainerVolumeArgs ContainerVolume(Account storageAccount, int vnetRangePrefix, string shareName, string mountPath)
    {
        var containerVolume = new GroupContainerVolumeArgs
        {
            Name = $"vol-{DeploymentName}-{shareName}-{vnetRangePrefix / 2 + 1}",
            MountPath = mountPath,
            StorageAccountName = storageAccount.Name,
            StorageAccountKey = storageAccount.PrimaryAccessKey,
            ShareName = FileShare(storageAccount, $"{shareName}{vnetRangePrefix / 2 + 1}").Name
        };

        return containerVolume;
    }

    private Share FileShare(Account storageAccount, string shareName)
    {
        var fileShare = new Share(shareName, new ShareArgs
        {
            StorageAccountName = storageAccount.Name
        });

        return fileShare;
    }

    private GroupContainerPortArgs ContainerPort(int port, string protocol)
    {
        var containerPort = new GroupContainerPortArgs
        {
            Port = port,
            Protocol = protocol
        };

        return containerPort;
    }

    private VirtualNetwork VNet(ResourceGroup resourceGroup, int vnetRangePrefix, bool isExternal)
    {
        var vnetType = "in";

        if (isExternal)
        {
            vnetType = "ex";
            vnetRangePrefix++;
        }

        var vnet = new VirtualNetwork($"vnet-{DeploymentName}-{vnetType}-{vnetRangePrefix / 2 + 1}", new VirtualNetworkArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AddressSpaces = { $"10.{vnetRangePrefix}.0.0/16" },
        });

        return vnet;
    }

    private Subnet InSNet(ResourceGroup resourceGroup, int vnetRangePrefix, VirtualNetwork inVNet)
    {
        var inSNet = new Subnet($"snet-{DeploymentName}-in-{vnetRangePrefix / 2 + 1}", new SubnetArgs
        {
            ResourceGroupName = resourceGroup.Name,
            VirtualNetworkName = inVNet.Name,
            AddressPrefixes = $"10.{vnetRangePrefix}.0.0/16",
            ServiceEndpoints = { "Microsoft.Storage" },
            Delegations = {
                    new SubnetDelegationArgs(){
                        Name = $"snet-delegation-{DeploymentName}-{vnetRangePrefix / 2 + 1}",
                        ServiceDelegation = new SubnetDelegationServiceDelegationArgs
                        {
                            Name = "Microsoft.ContainerInstance/containerGroups"
                        }
                    }
                }
        });

        return inSNet;
    }

    private Profile NetworkProfile(ResourceGroup resourceGroup, int vnetRangePrefix, Subnet inSNet)
    {
        var networkProfile = new Profile($"np-{DeploymentName}-{vnetRangePrefix / 2 + 1}", new ProfileArgs
        {
            Location = resourceGroup.Location,
            ResourceGroupName = resourceGroup.Name,
            ContainerNetworkInterface = new ProfileContainerNetworkInterfaceArgs
            {
                Name = $"nic-{DeploymentName}-{vnetRangePrefix / 2 + 1}",
                IpConfigurations =
                {
                    new ProfileContainerNetworkInterfaceIpConfigurationArgs 
                    {
                        Name = $"ipconfig-{DeploymentName}-{vnetRangePrefix / 2 + 1}",
                        SubnetId = inSNet.Id
                    }
                }
            }
        });

        return networkProfile;
    }

    private Subnet ExSNet(ResourceGroup resourceGroup, int vnetRangePrefix, VirtualNetwork exVNet)
    {
        var exSNet = new Subnet($"snet-{DeploymentName}-ex-{vnetRangePrefix / 2 + 1}", new SubnetArgs
        {
            Name = "AzureFirewallSubnet",
            ResourceGroupName = resourceGroup.Name,
            VirtualNetworkName = exVNet.Name,
            AddressPrefixes = $"10.{vnetRangePrefix + 1}.0.0/16",
        });

        return exSNet;
    }

    private void RegionPeering(Output<string> resourceGroupName, VirtualNetwork inVNet, VirtualNetwork exVNet, int vnetRangePrefix)
    {
        var inVNetPeer = new VirtualNetworkPeering($"peer-vnet{vnetRangePrefix / 2 + 1}-in", new VirtualNetworkPeeringArgs
        {
            ResourceGroupName = resourceGroupName,
            VirtualNetworkName = inVNet.Name,
            RemoteVirtualNetworkId = exVNet.Id,
            AllowForwardedTraffic = true
        });

        var exVNetPeer = new VirtualNetworkPeering($"peer-vnet{vnetRangePrefix / 2 + 1}-ex", new VirtualNetworkPeeringArgs
        {
            ResourceGroupName = resourceGroupName,
            VirtualNetworkName = exVNet.Name,
            RemoteVirtualNetworkId = inVNet.Id,
            AllowForwardedTraffic = true
        });
    }

    private Firewall Firewall(ResourceGroup resourceGroup, Subnet snet, int vnetRangePrefix)
    {
        var publicIp = new PublicIp($"ip-fw-{DeploymentName}{vnetRangePrefix / 2 + 1}", new PublicIpArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            Sku = "Standard",
            AllocationMethod = "Static"
        });

        var firewall = new Firewall($"fw-{DeploymentName}{vnetRangePrefix / 2 + 1}", new FirewallArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            IpConfigurations =
            {
                new FirewallIpConfigurationArgs
                {
                    Name = $"fw-config-{DeploymentName}{vnetRangePrefix / 2 + 1}",
                    SubnetId = snet.Id,
                    PublicIpAddressId = publicIp.Id
                }
            }
        });

        return firewall;
    }

    private void RegionRouteTable(ResourceGroup resourceGroup, Subnet snet, int vnetRangePrefix, Firewall firewall)
    {
        firewall.IpConfigurations
            .GetAt(0)
            .Apply(config =>
                new RouteTable($"route-{vnetRangePrefix / 2 + 1}-in", new RouteTableArgs
                {
                    ResourceGroupName = resourceGroup.Name,
                    Location = resourceGroup.Location,
                    Routes = Enumerable
                        .Range(1, locations.Count())
                        .Where(prefix => prefix != vnetRangePrefix / 2 + 1)
                        .Select(prefix => new RouteTableRouteArgs
                        {
                            Name = $"route-{vnetRangePrefix / 2 + 1}-to-{prefix}",
                            AddressPrefix = $"10.{prefix * 2 - 1}.0.0/16",
                            NextHopType = "VirtualAppliance",
                            NextHopInIpAddress = config.PrivateIpAddress ?? string.Empty
                        })
                        .ToArray()
                }))
            .Apply(route => 
                new SubnetRouteTableAssociation($"route-assoc-{vnetRangePrefix / 2 + 1}-in", new SubnetRouteTableAssociationArgs
                {
                    RouteTableId = route.Id,
                    SubnetId = snet.Id
                }));
    }

    private Account Storage(ResourceGroup resourceGroup, int vnetRangePrefix)
    {
        var storageAccount = new Account($"strg{DeploymentName}{vnetRangePrefix / 2 + 1}", new AccountArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = resourceGroup.Location,
            AccountKind = "StorageV2",
            AccountReplicationType = "LRS",
            AccountTier = "Standard"
        });

        return storageAccount;
    }

    private void DnsRecord(Output<string> resourceGroupName, Zone privateDns, Output<string> ipAddress, int vnetRangePrefix, Resource[] dependencies)
    {
        var nodeRecord = new ARecord($"a-{DeploymentName}{vnetRangePrefix / 2 + 1}", new ARecordArgs
        {
            ResourceGroupName = resourceGroupName,
            ZoneName = privateDns.Name,
            Name = $"{DeploymentName}{vnetRangePrefix / 2 + 1}",
            Records = { ipAddress },
            Ttl = 300
        },
        new CustomResourceOptions
        {
            DependsOn = dependencies
        });
    }

    private void ReverseDnsRecord(Output<string> resourceGroupName, Zone privateReverseDns, Output<string> ipAddress, int vnetRangePrefix)
    {
        var nodeRecord = new PTRRecord($"ptr-{DeploymentName}{vnetRangePrefix / 2 + 1}", new PTRRecordArgs
        {
            ResourceGroupName = resourceGroupName,
            ZoneName = privateReverseDns.Name,
            Name = ipAddress.Apply(ipAddress => ConstructReveseIpAddress(ipAddress, 3)),
            Records = { $"{DeploymentName}{vnetRangePrefix / 2 + 1}.{ZoneName}" },
            Ttl = 300
        });
    }

    private string ConstructReveseIpAddress(string ipAddress, int requriedSegments)
    {
        var segments = ipAddress
            .Split('.')
            .Reverse()
            .Take(requriedSegments);

        var reverseIp = string.Join('.', segments);

        return reverseIp;
    }

    private IEnumerable<FirewallNetworkRuleCollection> IpForwarding(Output<string>[] ipAddresses, Firewall[] firewalls)
    {
        return firewalls
            .Select((firewall, currFirewall) =>
                new FirewallNetworkRuleCollection($"fw-rules-{currFirewall + 1}-in", new FirewallNetworkRuleCollectionArgs
                {
                    ResourceGroupName = firewall.ResourceGroupName,
                    AzureFirewallName = firewall.Name,
                    Action = "Allow",
                    Priority = 100,
                    Rules = {
                        new FirewallNetworkRuleCollectionRuleArgs {
                            Name = $"rule-from-{currFirewall + 1}-in",
                            SourceAddresses = { ipAddresses[currFirewall] },
                            DestinationAddresses = ipAddresses
                                .Where((ApplicationGatewayBackendAddressPoolArgs, index) => index != currFirewall)
                                .ToList(),
                            Protocols = { "TCP" },
                            DestinationPorts = { "*" }
                        },
                        new FirewallNetworkRuleCollectionRuleArgs {
                            Name = $"rule-to-{currFirewall + 1}-in",
                            SourceAddresses = ipAddresses
                                .Where((ApplicationGatewayBackendAddressPoolArgs, index) => index != currFirewall)
                                .ToList(),
                            DestinationAddresses = { ipAddresses[currFirewall] },
                            Protocols = { "TCP" },
                            DestinationPorts = { "*" }
                        }
                    }
                }));
    }

    private ARecord DiscoveryDnsRecord(Output<string> resourceGroupName, Zone privateDns, Output<string>[] ipAddresses)
    {
        var discoveryRecord = new ARecord($"a-{DiscoveryRecordName}", new ARecordArgs
        {
            ResourceGroupName = resourceGroupName,
            ZoneName = privateDns.Name,
            Name = DiscoveryRecordName,
            Records = ipAddresses,
            Ttl = 300
        });

        return discoveryRecord;
    }

    private void GlobalPeering(VirtualNetwork[] vnets)
    {
        for (int currVNetEx = 0; currVNetEx < vnets.Length; currVNetEx++)
        {
            for (int currVNetIn = 0; currVNetIn < vnets.Length; currVNetIn++)
            {
                if (vnets[currVNetEx] != vnets[currVNetIn])
                {
                    var vnetPeer = new VirtualNetworkPeering($"peer-vnet{currVNetEx + 1}-vnet{currVNetIn + 1}", new VirtualNetworkPeeringArgs
                    {
                        ResourceGroupName = vnets[currVNetEx].ResourceGroupName,
                        VirtualNetworkName = vnets[currVNetEx].Name,
                        RemoteVirtualNetworkId = vnets[currVNetIn].Id,
                        AllowForwardedTraffic = true
                    });
                }
            }
        }
    }

    private void GlobalRoutes(VirtualNetwork[] vnets, Subnet[] snets, Firewall[] firewalls)
    {
        for (int currVnet = 0; currVnet < vnets.Length; currVnet++)
        {
            var route = new RouteTable($"route-{currVnet + 1}-ex", new RouteTableArgs
            {
                ResourceGroupName = vnets[currVnet].ResourceGroupName,
                Location = vnets[currVnet].Location,
                Routes = Enumerable
                    .Range(0, locations.Count())
                    .Where(currFirewall => currFirewall != currVnet)
                    .Select(currFirewall => FirewallRoute(firewalls[currFirewall], currVnet, currFirewall))
                    .Concat(new[] { InternetRoute(currVnet) })
                    .ToArray()
            });

            var association = new SubnetRouteTableAssociation($"route-assoc-{currVnet + 1}-ex", new SubnetRouteTableAssociationArgs
            {
                RouteTableId = route.Id,
                SubnetId = snets[currVnet].Id
            });
        }
    }

    private Output<RouteTableRouteArgs> FirewallRoute(Firewall firewall, int currVnet, int currFirewall)
    {
        return firewall.IpConfigurations
            .GetAt(0)
            .Apply(config => new RouteTableRouteArgs
            {
                Name = $"route-{currVnet + 1}-to-fw{currFirewall + 1}",
                AddressPrefix = $"10.{currFirewall * 2 + 1}.0.0/16",
                NextHopType = "VirtualAppliance",
                NextHopInIpAddress = config.PrivateIpAddress ?? string.Empty
            });
    }

    private Output<RouteTableRouteArgs> InternetRoute(int currVnet)
    {
        return Output
            .Create(new RouteTableRouteArgs
            {
                Name = $"route-{currVnet + 1}-to-internet",
                AddressPrefix = "0.0.0.0/0",
                NextHopType = "Internet"
            });
    }
}
