namespace EdgeOpcUAClient
{
    using System;
    using System.Collections.Generic;
    using System.Reactive.Disposables;
    using System.Threading.Tasks;
    using System.Threading;
    using System.Runtime.Loader;
    using Opc.Ua;
    using Opc.Ua.Client;
    using System.Security.Cryptography.X509Certificates;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;
    using System.Timers;
    using System.Net;
    using System.Net.Sockets;
    using Newtonsoft.Json;
    using System.Text;

    public class Module
    {
        private const string OpcUAConnectionStringKey = nameof(ModuleConfig.OpcUAConnectionString);
        private const string OpcUASampleValueKey = nameof(ModuleConfig.OpcUASampleValue);
        private readonly TimeSpan retryInterval = TimeSpan.FromSeconds(5);
        private string opcUAConnectionString = "opc.tcp://opc-server:51210/UA/SampleServer";

        public async Task StartUp()
        {
            string connectionString = Environment.GetEnvironmentVariable("EdgeHubConnectionString");
            var disposable = await Init(connectionString);

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            await WhenCancelled(cts.Token);

            disposable.Dispose();
        }

        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the DeviceClient and sets up the callback to receive messages
        /// </summary>
        private async Task<IDisposable> Init(string connectionString)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            // Use Mqtt transport settings.
            // The RemoteCertificateValidationCallback needs to be set
            // since the Edge Hub currently uses a self signed SSL certificate.
            ITransportSettings[] settings =
            {
                new MqttTransportSettings(TransportType.Mqtt_Tcp_Only)
                { RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true }
            };

            // Open a connection to the Edge runtime
            DeviceClient deviceClient =
                DeviceClient.CreateFromConnectionString(connectionString, settings);
            await deviceClient.OpenAsync();
            Console.WriteLine("EdgeOpcUAClient - Opened module client connection");

            ModuleConfig moduleConfig = await GetConfiguration(deviceClient);
            await deviceClient.SetDesiredPropertyUpdateCallbackAsync( (props, context) => UpdateDesiredProperties(props, context, moduleConfig), deviceClient);

            var session = await OpcBoot(moduleConfig);

            await deviceClient.SetInputMessageHandlerAsync(
                "input1",
                (message, context) => SendDataToDevice(deviceClient, session, message, moduleConfig),
                null);
            Console.WriteLine("EdgeOpcUAClient - callback for route \"input1\" registered");

            return Disposable.Create(() =>
            {
                session.Close();
                session.Dispose();
            });
        }

        private async Task<MessageResponse> SendDataToDevice(DeviceClient deviceClient, Session session, Message message, ModuleConfig moduleConfig)
        {
            return await Task.Run( () => {
                byte[] messageBytes = message.GetBytes();
                string messageString = Encoding.UTF8.GetString(messageBytes);
                Console.WriteLine($"Message received: {messageString}");

                // send it to the OPC-UA device using the session.
                return MessageResponse.Completed;
            });
        }


        private async Task UpdateDesiredProperties(TwinCollection desiredproperties, object usercontext, ModuleConfig moduleConfig)
        {
            if (desiredproperties.Contains(OpcUASampleValueKey))
            {
                string value = desiredproperties[OpcUASampleValueKey].ToString();
                if(!string.IsNullOrEmpty(value))
                {
                    // moduleConfig.OpcUAConnectionString = "opc.tcp://opc-server:51210/UA/SampleServer";
                    moduleConfig.OpcUASampleValue = value;
                }
                Console.WriteLine($"{nameof(ModuleConfig.OpcUASampleValue)}: { moduleConfig.OpcUASampleValue}");
            }

            if (usercontext is DeviceClient deviceClient)
            {
                await deviceClient.UpdateReportedPropertiesAsync(CreateTwinCollectionFromModuleConfig(moduleConfig));
            }
        }

        private TwinCollection CreateTwinCollectionFromModuleConfig(ModuleConfig moduleConfig)
        {
            return new TwinCollection(JsonConvert.SerializeObject(moduleConfig));
        }

        private async Task<Session> OpcBoot(ModuleConfig moduleConfig)
        {
            Console.WriteLine($"EdgeOpcUAClient - Message received at {DateTime.Now.ToLongTimeString()}");
            while(true)
            {
                try
                {
                    return await ConnectToServer(moduleConfig.OpcUAConnectionString);
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"Message: {ex.Message}");
                    Console.WriteLine($"Error on connection to OPC UA endpoint. Retry in {retryInterval.TotalSeconds} seconds");
                    await Task.Delay(retryInterval);
                }
            }
        }
        async Task<Session> ConnectToServer(string endpointUrl)
        {
            var config = await CreateConfiguration();
            var selectedEndpoint = DiscoverEndpoints(endpointUrl, config);
            var session = await CreateSession(config, selectedEndpoint);

            return session;
        }

        private static async Task<ApplicationConfiguration> CreateConfiguration()
        {
            Console.WriteLine("1 - Create an Application Configuration.");
            Utils.SetTraceOutput(Utils.TraceOutput.DebugAndFile);
            var config = new ApplicationConfiguration
            {
                ApplicationName = "UA Core Sample Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = "urn:" + Utils.GetHostName() + ":OPCFoundation:CoreSampleClient",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "X509Store",
                        StorePath = "CurrentUser\\UA_MachineDefault",
                        SubjectName = "PLC-1/OPCUA-1-6"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "OPC Foundation/CertificateStores/UA Applications"
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "OPC Foundation/CertificateStores/UA Certificate Authorities"
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "OPC Foundation/CertificateStores/RejectedCertificates"
                    },
                    NonceLength = 32,
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas {OperationTimeout = 15000},
                ClientConfiguration = new ClientConfiguration {DefaultSessionTimeout = 60000}
            };

            await config.Validate(ApplicationType.Client);

            var haveAppCertificate = HasAppCertificate(config);
            if (!haveAppCertificate)
            {
                Console.WriteLine("    INFO: Creating new application certificate: {0}", config.ApplicationName);

                var certificate = CertificateFactory.CreateCertificate(
                    config.SecurityConfiguration.ApplicationCertificate.StoreType,
                    config.SecurityConfiguration.ApplicationCertificate.StorePath,
                    null,
                    config.ApplicationUri,
                    config.ApplicationName,
                    config.SecurityConfiguration.ApplicationCertificate.SubjectName,
                    null,
                    CertificateFactory.defaultKeySize,
                    DateTime.UtcNow - TimeSpan.FromDays(1),
                    CertificateFactory.defaultLifeTime,
                    CertificateFactory.defaultHashSize,
                    false,
                    null,
                    null
                );

                config.SecurityConfiguration.ApplicationCertificate.Certificate = certificate;
            }

            haveAppCertificate = config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

            if (haveAppCertificate)
            {
                config.ApplicationUri = Utils.GetApplicationUriFromCertificate(config.SecurityConfiguration.ApplicationCertificate.Certificate);

                if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                {
                    config.CertificateValidator.CertificateValidation += CertificateValidator_CertificateValidation;
                }
            }
            else
            {
                Console.WriteLine("    WARN: missing application certificate, using unsecure connection.");
            }
            return config;
        }

        private static async Task<Session> CreateSession(ApplicationConfiguration config, EndpointDescription selectedEndpoint)
        {
            Console.WriteLine("3 - Create a session with OPC UA server.");
            var endpointConfiguration = EndpointConfiguration.Create(config);
            var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
            var session = await Session.Create(config, endpoint, true, ".Net Core OPC UA Console Client", 60000, new UserIdentity(new AnonymousIdentityToken()), null);
            return session;
        }

        private static EndpointDescription DiscoverEndpoints(string endpointUrl, ApplicationConfiguration config)
        {
            var haveAppCertificate = HasAppCertificate(config);

            Console.WriteLine("2 - Discover endpoints of {0}.", endpointUrl);
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointUrl, haveAppCertificate);
            Console.WriteLine("    Selected endpoint uses: {0}", selectedEndpoint.SecurityPolicyUri.Substring(selectedEndpoint.SecurityPolicyUri.LastIndexOf('#') + 1));

            return selectedEndpoint;
        }

        private static bool HasAppCertificate(ApplicationConfiguration config)
        {
            return config.SecurityConfiguration.ApplicationCertificate.Certificate != null;
        }

        private static void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            foreach (var value in item.DequeueValues())
            {
                Console.WriteLine("{0}: {1}, {2}, {3}", item.DisplayName, value.Value, value.SourceTimestamp, value.StatusCode);
            }
        }

        private static void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            Console.WriteLine("Accepted Certificate: {0}", e.Certificate.Subject);
            e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted);
        }

        /// <summary>
        /// Get the configuration for the module (in this case the threshold temperature)s.
        /// </summary>
        private async Task<ModuleConfig> GetConfiguration(DeviceClient deviceClient)
        {
            // First try to get the config from the Module twin
            var twin = await deviceClient.GetTwinAsync();

            if (twin.Properties.Desired.Contains(OpcUAConnectionStringKey))
            {
                opcUAConnectionString = (string) twin.Properties.Desired[OpcUAConnectionStringKey];
            }

            var moduleConfig = new ModuleConfig(opcUAConnectionString);
            await UpdateDesiredProperties(twin.Properties.Desired, deviceClient, moduleConfig);

            Console.WriteLine(moduleConfig);
            return moduleConfig;

        }
    }
}
