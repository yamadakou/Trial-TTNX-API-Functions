using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Resources;
using System.Collections.Generic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Network;

namespace Dcinc.TrialTTNX
{
    public class TrialTTNXFunction
    {
        private readonly ILogger<TrialTTNXFunction> _logger;

        public TrialTTNXFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<TrialTTNXFunction>();
        }

        private class RequestData
        {
            public string UserName { get; set; } = string.Empty;
        }

        [Function("trial-ttnx")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonSerializer.Deserialize<RequestData>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var response = req.CreateResponse();

            if (string.IsNullOrWhiteSpace(data?.UserName))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("Please provide a valid username in the request body.");
                return response;
            }

            string resourceGroupName = data.UserName;
            const string location = "JapanEast";
            string subscriptionId = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new InvalidOperationException("Subscription ID is not set.");
            }
            string password = Environment.GetEnvironmentVariable("DB_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("db password is not set.");
            }

            try
            {
                var armClient = new ArmClient(new DefaultAzureCredential());
                var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
                var resourceGroupCollection = subscription.GetResourceGroups();

                if(resourceGroupCollection.Any(r => r.Data.Name == resourceGroupName))
                {
                    throw new InvalidOperationException($"The resource group '{resourceGroupName}' has already been created. Please specify a different resource group name.");
                }

                // **���\�[�X�O���[�v�̍쐬**
                var resourceGroup = await resourceGroupCollection.CreateOrUpdateAsync(
                    Azure.WaitUntil.Completed,
                    resourceGroupName,
                    new ResourceGroupData(location));

                _logger.LogInformation($"Resource group '{resourceGroupName}' created successfully in '{location}'.");

                // VNet ���쐬
                var vnetCollection = resourceGroup.Value.GetVirtualNetworks();
                var vnetName = $"{resourceGroupName}-VNet";
                var subnetName = $"{resourceGroupName}-Subnet";

                var subnetData = new SubnetData
                {
                    Name = subnetName,
                    AddressPrefix = "10.0.0.0/23"
                };

                var vnetData = new VirtualNetworkData
                {
                    Location = location,
                    AddressPrefixes = { "10.0.0.0/16" },
                    Subnets = { subnetData }
                };

                var vnet = await vnetCollection.CreateOrUpdateAsync(
                    Azure.WaitUntil.Completed,
                    vnetName,
                    vnetData);

                _logger.LogInformation($"Virtual Network '{vnetName}' created successfully.");

                // **�T�u�l�b�g�̃��\�[�XID���擾**
                var subnet = vnet.Value.Data.Subnets[0];
                var subnetId = subnet.Id;
                _logger.LogInformation($"Subnet resource ID: {subnetId}");

                // **Container Apps ���̍쐬**
                var containerEnvCollection = resourceGroup.Value.GetContainerAppManagedEnvironments();
                var containerEnvName = $"{resourceGroupName}-ContainerAppEnv";

                var containerEnvData = new ContainerAppManagedEnvironmentData(location)
                {
                    VnetConfiguration = new ContainerAppVnetConfiguration()
                    {
                        InfrastructureSubnetId = subnetId
                    }
                };

                var managedEnv = await containerEnvCollection.CreateOrUpdateAsync(
                    Azure.WaitUntil.Completed,
                    containerEnvName,
                    containerEnvData);

                _logger.LogInformation($"Container App Environment '{containerEnvName}' created successfully.");

                // **Redis �̃f�v���C**
                await CreateContainerApp(resourceGroup.Value, managedEnv.Value, "redis", "redis:latest", 6379,
                ingress: new ContainerAppIngressConfiguration
                {
                    External = false,  // �O���A�N�Z�X��L����
                    TargetPort = 6379,
                    Transport = ContainerAppIngressTransportMethod.Tcp,
                    ExposedPort = 6379
                });

                // **PostgreSQL �̃f�v���C**
                await CreateContainerApp(resourceGroup.Value, managedEnv.Value, "db", "postgres:16", 5432, 1.0, "2Gi", 3, new[]
                {
                    new ContainerAppEnvironmentVariable { Name = "POSTGRES_USER", Value = "postgres" },
                    new ContainerAppEnvironmentVariable { Name = "POSTGRES_PASSWORD", Value = password }
                },
                new ContainerAppIngressConfiguration
                {
                    External = false,  // �O���A�N�Z�X��L����
                    TargetPort = 5432,
                    Transport = ContainerAppIngressTransportMethod.Tcp,
                    ExposedPort = 5432
                });

                // **Web �A�v�� (TimeTracker) �̃f�v���C**
                var webAppFqdn = await CreateContainerApp(resourceGroup.Value, managedEnv.Value, "web", "densocreate/timetracker:7.0-linux-postgres", 8080, 0.75, "1.5Gi", 1, new[]
                {
                    new ContainerAppEnvironmentVariable { Name = "TTNX_DB_TYPE", Value = "postgresql" },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_DB_SERVER", Value = "db" },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_DB_PORT", Value = "5432" },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_DB_USER", Value = "postgres" },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_DB_PASSWORD", Value = password },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_REDIS_GLOBALCACHE", Value = "redis:6379" },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_REDIS_HANGFIRE", Value = "redis:6379" },
                    new ContainerAppEnvironmentVariable { Name = "TTNX_REDIS_BACKGROUNDJOB", Value = "redis:6379" }
                },
                new ContainerAppIngressConfiguration
                {
                    External = true,  // �O���A�N�Z�X��L����
                    TargetPort = 8080,
                    Transport = ContainerAppIngressTransportMethod.Auto
                });

                _logger.LogInformation($"Container Apps deployed successfully.");

                var result = new Dictionary<string, string>
                {
                    { "WebAppFQDN", webAppFqdn },
                    { "ResourceGroupName", resourceGroupName   },
                    { "VNetName", vnetName },
                    { "SubnetName", subnetName },
                    { "Message", "Container Apps deployed successfully." }
                };
                await response.WriteAsJsonAsync(result);
                response.StatusCode = HttpStatusCode.OK;
            }
            catch (Exception ex)
            {
                _logger.LogError($"An error occurred: {ex.Message}");
                response.StatusCode = HttpStatusCode.InternalServerError;

                var result = new Dictionary<string, string>
                {
                    { "Message", $"Failed to configure resources. Error: {ex.Message}" }
                };
                await response.WriteAsJsonAsync(result);
            }

            return response;
        }

        // **�R���e�i�A�v�����쐬���郁�\�b�h**
        private async Task<string?> CreateContainerApp(ResourceGroupResource resourceGroup, ContainerAppManagedEnvironmentResource environment, string name, string image, int port, double cpuSize = 0.5, string memorySize = "1Gi", int maxReplicas = 3, ContainerAppEnvironmentVariable[] envVars = null, ContainerAppIngressConfiguration ingress = null)
        {
            var container = new ContainerAppContainer()
            {
                Name = name,
                Image = image,
                Resources = new AppContainerResources()
                {
                    Cpu = cpuSize,
                    Memory = memorySize
                }
            };
            if (envVars != null)
            {
                foreach (var envVar in envVars)
                {
                    container.Env.Add(envVar);
                }
            }

            var containerAppData = new ContainerAppData(resourceGroup.Data.Location)
            {
                EnvironmentId = environment.Id,
                Configuration = new ContainerAppConfiguration(),
                Template = new ContainerAppTemplate()
                {
                    Containers =
                    {
                        container
                    },
                    Scale = new ContainerAppScale()
                    {
                        MinReplicas = 1,
                        MaxReplicas = maxReplicas
                    }
                }
            };

            if(ingress != null)
            {
                containerAppData.Configuration.Ingress = ingress;
            }

            var containerAppResource = await resourceGroup.GetContainerApps().CreateOrUpdateAsync(Azure.WaitUntil.Completed, name, containerAppData);

            // �R���e�i�A�v���̃G���h�|�C���g�iFQDN�j���擾
            string? fqdn = containerAppResource.Value.Data.Configuration?.Ingress?.Fqdn;
            if (!string.IsNullOrEmpty(fqdn))
            {
                if(containerAppResource.Value.Data.Configuration?.Ingress?.Transport == ContainerAppIngressTransportMethod.Tcp)
                {
                    fqdn = $"{fqdn}:{port}";
                }
            }

            _logger.LogInformation($"Container App '{name}' created successfully.fqdn={fqdn}");

            return fqdn;
        }
    }
}
