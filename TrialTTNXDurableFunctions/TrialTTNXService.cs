//using Castle.Core.Logging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Resources;
using System.Net;
using System.Text.Json;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Network;
using Azure;
using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;

namespace TrialTTNXDurableFunctions
{
    public static class TrialTTNXService
    {

        private class RequestData
        {
            public string UserName { get; set; } = string.Empty;
            public string Location { get; set; } = "JapanEast";
        }

        public class ParameterData
        {
            public string Location { get; set; } = "JapanEast";
            public string ResourceGroupName { get; set; } = string.Empty;
            public string SubscriptionId { get; set; } = string.Empty;
            public string VnetName { get; set; } = string.Empty;
            public string SubnetName { get; set; } = string.Empty;
            public string? ContainerEnvName { get; set; } = string.Empty;
        }

        public class ResultData
        {
            public bool IsSuccess { get; set; } = false;
            public string Message { get; set; } = string.Empty;
            public string WebAppFQDN { get; set; } = string.Empty;
            public string ResourceGroupName { get; set; } = string.Empty;
            public string VnetName { get; set; } = string.Empty;
            public string SubnetName { get; set; } = string.Empty;
            public string? ContainerEnvName { get; set; } = string.Empty;
        }

        [Function(nameof(TrialTTNXService))]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            ILogger logger = context.CreateReplaySafeLogger(nameof(TrialTTNXService));
            logger.LogInformation("Do TrialTTNXService.");
            var outputs = new List<string>();

            try
            {
                string? requestBody = context.GetInput<string>();
                if (string.IsNullOrEmpty(requestBody))
                {
                    throw new InvalidOperationException("Input object cannot be null or empty.");
                }
                var data = JsonSerializer.Deserialize<RequestData>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if(data == null)
                {
                    throw new InvalidOperationException("RequestBody cannot be null or empty.");
                }

                if (string.IsNullOrEmpty(data.UserName))
                {
                    throw new InvalidOperationException("UserName cannot be null or empty.");
                }

                string? subscriptionId = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                if (string.IsNullOrEmpty(subscriptionId))
                {
                    throw new InvalidOperationException("Subscription ID is not set.");
                }

                var parameterData = new ParameterData
                {
                    ResourceGroupName = data.UserName,
                    Location = data.Location,
                    SubscriptionId = subscriptionId
                };

                // Durable Functions Activity
                context.SetCustomStatus("Start");

                // Crate Resource Group
                context.SetCustomStatus("Begin.Crate.ResourceGroup");
                var resultResourceGroup = await context.CallActivityAsync<ResultData>(nameof(TrialTTNXCreateResourceGroup), parameterData);
                if(!resultResourceGroup.IsSuccess)
                {
                    throw new InvalidOperationException($"Failed to create resource group. (resourceGroupName = {parameterData.ResourceGroupName})");
                }
                context.SetCustomStatus("End.Crate.ResourceGroup");

                // Crate Virtual Network
                context.SetCustomStatus("Begin.Crate.VirtualNetwork");
                var resultVirtualNetwork = await context.CallActivityAsync<ResultData>(nameof(TrialTTNXCreateVirtualNetwork), parameterData);
                if (!resultVirtualNetwork.IsSuccess)
                {
                    throw new InvalidOperationException($"Failed to create virtual network. (resourceGroupName = {parameterData.ResourceGroupName})");
                }
                parameterData.VnetName = resultVirtualNetwork.VnetName;
                parameterData.SubnetName = resultVirtualNetwork.SubnetName;
                context.SetCustomStatus("End.Crate.VirtualNetwork");

                // Crate Container App Environment
                context.SetCustomStatus("Begin.Crate.ContainerAppEnvironment");
                var resultContainerAppEnvironment = await context.CallActivityAsync<ResultData>(nameof(TrialTTNXCreateContainerAppEnvironment), parameterData);
                if (!resultContainerAppEnvironment.IsSuccess)
                {
                    throw new InvalidOperationException($"Failed to create container app environment. (resourceGroupName = {parameterData.ResourceGroupName})");
                }
                parameterData.ContainerEnvName = resultContainerAppEnvironment.ContainerEnvName;
                context.SetCustomStatus("End.Crate.ContainerAppEnvironment");

                // Deploy Container Apps
                context.SetCustomStatus("Begin.Deploy.ContainerApps");
                var resultDeployContainerApps = await context.CallActivityAsync<ResultData>(nameof(TrialTTNXDeployContainerApps), parameterData);
                if (!resultDeployContainerApps.IsSuccess)
                {
                    throw new InvalidOperationException($"Failed to deploy container apps. (resourceGroupName = {parameterData.ResourceGroupName})");
                }
                context.SetCustomStatus("End.Deploy.ContainerApps");

                outputs.Add(resultDeployContainerApps.WebAppFQDN);

                context.SetCustomStatus("End");

            }
            catch (Exception ex)
            {
                context.SetCustomStatus("Failed");
                logger.LogError($"An error occurred: {ex.ToJson()}");
                outputs.Add(ex.ToJson());
            }

            return outputs;
        }

        [Function(nameof(TrialTTNXCreateResourceGroup))]
        public static async Task<ResultData> TrialTTNXCreateResourceGroup([ActivityTrigger] ParameterData parameterData, FunctionContext executionContext)
        {
            ILogger _logger = executionContext.GetLogger("TrialTTNXCreateResourceGroup");
            var location = parameterData.Location;
            var resourceGroupName = parameterData.ResourceGroupName;
            _logger.LogInformation($"TrialTTNXCreateResourceGroup: Begin: resourceGroupName = {resourceGroupName}.");

            var armClient = new ArmClient(new DefaultAzureCredential());
            var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{parameterData.SubscriptionId}"));
            var resourceGroupCollection = subscription.GetResourceGroups();

            if (resourceGroupCollection.Any(r => r.Data.Name == resourceGroupName))
            {
                throw new InvalidOperationException($"The resource group '{resourceGroupName}' has already been created. Please specify a different resource group name.");
            }

            // **リソースグループの作成**
            var resourceGroup = await resourceGroupCollection.CreateOrUpdateAsync(
                Azure.WaitUntil.Completed,
                resourceGroupName,
            new ResourceGroupData(location));

            _logger.LogInformation($"Resource group '{resourceGroupName}' created successfully in '{location}'.");

            var result = new ResultData
            {
                IsSuccess = true,
                ResourceGroupName = resourceGroupName,
                Message = $"Resource group '{resourceGroupName}' created successfully in '{location}'."
            };

            _logger.LogInformation($"TrialTTNXCreateResourceGroup: End: resourceGroupName = {resourceGroupName}.");

            return result;
        }

        [Function(nameof(TrialTTNXCreateVirtualNetwork))]
        public static async Task<ResultData> TrialTTNXCreateVirtualNetwork([ActivityTrigger] ParameterData parameterData, FunctionContext executionContext)
        {
            ILogger _logger = executionContext.GetLogger("TrialTTNXCreateVirtualNetwork");
            var location = parameterData.Location;
            var resourceGroupName = parameterData.ResourceGroupName;
            _logger.LogInformation($"TrialTTNXCreateVirtualNetwork: Begin: resourceGroupName = {resourceGroupName}.");

            var armClient = new ArmClient(new DefaultAzureCredential());
            var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{parameterData.SubscriptionId}"));
            var resourceGroupCollection = subscription.GetResourceGroups();

            if (!resourceGroupCollection.Any(r => r.Data.Name == resourceGroupName))
            {
                throw new InvalidOperationException($"The resource group '{resourceGroupName}'does not exist.");
            }

            // **リソースグループの取得**
            var resourceGroup = await resourceGroupCollection.GetAsync(resourceGroupName);

            // VNet を作成
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

            var result = new ResultData
            {
                IsSuccess = true,
                ResourceGroupName = resourceGroupName,
                VnetName = vnetName,
                SubnetName = subnetName,
                Message = $"Virtual Network '{vnetName}' created successfully.(VnetName = {vnetName}, SubnetName = {subnetName})"
            };

            _logger.LogInformation($"TrialTTNXCreateVirtualNetwork: End: resourceGroupName = {resourceGroupName}.");
            return result;
        }

        [Function(nameof(TrialTTNXCreateContainerAppEnvironment))]
        public static async Task<ResultData> TrialTTNXCreateContainerAppEnvironment([ActivityTrigger] ParameterData parameterData, FunctionContext executionContext)
        {
            ILogger _logger = executionContext.GetLogger("TrialTTNXCreateContainerAppEnvironment");
            var location = parameterData.Location;
            var resourceGroupName = parameterData.ResourceGroupName;
            _logger.LogInformation($"TrialTTNXCreateContainerAppEnvironment: Begin: resourceGroupName = {resourceGroupName}.");

            var armClient = new ArmClient(new DefaultAzureCredential());
            var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{parameterData.SubscriptionId}"));
            var resourceGroupCollection = subscription.GetResourceGroups();

            if (!resourceGroupCollection.Any(r => r.Data.Name == resourceGroupName))
            {
                throw new InvalidOperationException($"The resource group '{resourceGroupName}'does not exist.");
            }

            // **リソースグループの取得**
            var resourceGroup = await resourceGroupCollection.GetAsync(resourceGroupName);

            // **サブネットのリソースIDを取得**
            var vnetCollection = resourceGroup.Value.GetVirtualNetworks();
            var vnet = await vnetCollection.GetAsync(parameterData.VnetName);
            var subnet = vnet.Value.Data.Subnets[0];
            var subnetId = subnet.Id;
            _logger.LogInformation($"Subnet resource ID: {subnetId}");

            // **Container Apps 環境の作成**
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

            var result = new ResultData
            {
                IsSuccess = true,
                ResourceGroupName = resourceGroupName,
                ContainerEnvName = containerEnvName,
                Message = $"Container App Environment '{containerEnvName}' created successfully."
            };

            _logger.LogInformation($"TrialTTNXCreateContainerAppEnvironment: End: resourceGroupName = {resourceGroupName}.");

            return result;
        }

        [Function(nameof(TrialTTNXDeployContainerApps))]
        public static async Task<ResultData> TrialTTNXDeployContainerApps([ActivityTrigger] ParameterData parameterData, FunctionContext executionContext)
        {
            ILogger _logger = executionContext.GetLogger("TrialTTNXDeployContainerApps");
            var location = parameterData.Location;
            var resourceGroupName = parameterData.ResourceGroupName;
            _logger.LogInformation($"TrialTTNXDeployContainerApps: Begin: resourceGroupName = {resourceGroupName}.");

            var armClient = new ArmClient(new DefaultAzureCredential());
            var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{parameterData.SubscriptionId}"));
            var resourceGroupCollection = subscription.GetResourceGroups();

            if (!resourceGroupCollection.Any(r => r.Data.Name == resourceGroupName))
            {
                throw new InvalidOperationException($"The resource group '{resourceGroupName}'does not exist.");
            }

            // **リソースグループの取得**
            var resourceGroup = await resourceGroupCollection.GetAsync(resourceGroupName);

            // **Container Apps 環境の取得**
            var containerEnvCollection = resourceGroup.Value.GetContainerAppManagedEnvironments();
            var managedEnv = await containerEnvCollection.GetAsync(parameterData.ContainerEnvName);

            // **Redis のデプロイ**
            await CreateContainerApp(_logger, resourceGroup.Value, managedEnv.Value, "redis", "redis:latest", 6379,
            ingress: new ContainerAppIngressConfiguration
            {
                External = false,  // 外部アクセスを有効化
                TargetPort = 6379,
                Transport = ContainerAppIngressTransportMethod.Tcp,
                ExposedPort = 6379
            });

            // **PostgreSQL のデプロイ**
            string? password = Environment.GetEnvironmentVariable("DB_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("db password is not set.");
            }
            await CreateContainerApp(_logger, resourceGroup.Value, managedEnv.Value, "db", "postgres:16", 5432, 1.0, "2Gi", 3, new[]
            {
                    new ContainerAppEnvironmentVariable { Name = "POSTGRES_USER", Value = "postgres" },
                    new ContainerAppEnvironmentVariable { Name = "POSTGRES_PASSWORD", Value = password }
                },
            new ContainerAppIngressConfiguration
            {
                External = false,  // 外部アクセスを有効化
                TargetPort = 5432,
                Transport = ContainerAppIngressTransportMethod.Tcp,
                ExposedPort = 5432
            });

            // **Web アプリ (TimeTracker) のデプロイ**
            var webAppFqdn = await CreateContainerApp(_logger, resourceGroup.Value, managedEnv.Value, "web", "densocreate/timetracker:7.0-linux-postgres", 8080, 0.75, "1.5Gi", 1, new[]
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
                External = true,  // 外部アクセスを有効化
                TargetPort = 8080,
                Transport = ContainerAppIngressTransportMethod.Auto
            });

            _logger.LogInformation($"Container Apps deployed successfully.");

            var result = new ResultData
            {
                IsSuccess = true,
                ResourceGroupName = resourceGroupName,
                WebAppFQDN = webAppFqdn,
                Message = $"Container Apps deployed successfully."
            };

            _logger.LogInformation($"TrialTTNXDeployContainerApps: End: resourceGroupName = {resourceGroupName}.");

            return result;
        }

        // **コンテナアプリを作成するメソッド**
        private static async Task<string?> CreateContainerApp(ILogger _logger, ResourceGroupResource resourceGroup, ContainerAppManagedEnvironmentResource environment, string name, string image, int port, double cpuSize = 0.5, string memorySize = "1Gi", int maxReplicas = 3, ContainerAppEnvironmentVariable[] envVars = null, ContainerAppIngressConfiguration ingress = null)
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

            if (ingress != null)
            {
                containerAppData.Configuration.Ingress = ingress;
            }

            var containerAppResource = await resourceGroup.GetContainerApps().CreateOrUpdateAsync(Azure.WaitUntil.Completed, name, containerAppData);

            // コンテナアプリのエンドポイント（FQDN）を取得
            string? fqdn = containerAppResource.Value.Data.Configuration?.Ingress?.Fqdn;
            if (!string.IsNullOrEmpty(fqdn))
            {
                if (containerAppResource.Value.Data.Configuration?.Ingress?.Transport == ContainerAppIngressTransportMethod.Tcp)
                {
                    fqdn = $"{fqdn}:{port}";
                }
            }

            _logger.LogInformation($"Container App '{name}' created successfully.fqdn={fqdn}");

            return fqdn;

        }

        [Function("TrialTTNXService_HttpStart")]
        public static async Task<HttpResponseData> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext)
        {
            ILogger logger = executionContext.GetLogger("TrialTTNXService_HttpStart");

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

            string? subscriptionId = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new InvalidOperationException("Subscription ID is not set.");
            }
            string? password = Environment.GetEnvironmentVariable("DB_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("db password is not set.");
            }

            // Function input comes from the request content.
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(TrialTTNXService), requestBody);

            logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            // Returns an HTTP 202 response with an instance management payload.
            // See https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-http-api#start-orchestration
            return await client.CreateCheckStatusResponseAsync(req, instanceId);
        }
    }

    public static class ExceptionExtensions
    {
        private class ExceptionInfo
        {
            public string Message { get; set; } = string.Empty;
            public string StackTrace { get; set; } = string.Empty;
            public ExceptionInfo InnerException { get; set; }
        }

        public static string ToJson(this Exception ex)
        {
            ExceptionInfo info = BuildExceptionInfo(ex);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            return JsonSerializer.Serialize(info, options);
        }

        private static ExceptionInfo BuildExceptionInfo(Exception ex)
        {
            if (ex == null) return null;

            return new ExceptionInfo
            {
                Message = ex.Message,
                StackTrace = ex.StackTrace ?? string.Empty, // Ensure StackTrace is not null
                InnerException = BuildExceptionInfo(ex.InnerException)
            };
        }
    }
}
