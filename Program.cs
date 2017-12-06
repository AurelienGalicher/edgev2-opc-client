namespace EdgeOpcUAClient
{
    using System;
    using System.Collections.Generic;
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

    class Program
    {
        static void Main(string[] args)
        {
            string connectionString = Environment.GetEnvironmentVariable("EdgeHubConnectionString");
            Init(connectionString).Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
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
        static async Task Init(string connectionString)
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
            var userContext = new Tuple<DeviceClient, ModuleConfig>(deviceClient, moduleConfig);

            await deviceClient.SetInputMessageHandlerAsync(
                "input1",
                OpcBoot,
                userContext);
            Console.WriteLine("EdgeOpcUAClient - callback for route \"input1\" registered");
        }

        static string GetCompleteEndpointURI()
        {
            var result = Environment.GetEnvironmentVariable("OpcUAServerEndpointURI");
            return result == null ? "opc.tcp://opc-server:51210/UA/SampleServer" : result;
        }

        static async Task<MessageResponse> OpcBoot(Message message, object userContext){
            Console.WriteLine($"EdgeOpcUAClient - Message received at {DateTime.Now.ToLongTimeString()}");
            // use OPC UA .Net Sample server
            string endpoint = GetCompleteEndpointURI();
            Task t = null;
            try
            {
                t = ConsoleSampleClient(endpoint);
                t.Wait();
                return MessageResponse.Completed;
            }
            catch (Exception e)
            {
                Console.WriteLine("Exit due to Exception: {0}", e.Message);
                return MessageResponse.Abandoned;
            }
        }

        static async Task ConsoleSampleClient(string endpointURL)
        {
            Console.WriteLine("1 - Create an Application Configuration.");
            Utils.SetTraceOutput(Utils.TraceOutput.DebugAndFile);
            var config = new ApplicationConfiguration()
            {
                ApplicationName = "UA Core Sample Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = "urn:"+Utils.GetHostName()+":OPCFoundation:CoreSampleClient",
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "X509Store",
                        StorePath = "CurrentUser\\UA_MachineDefault",
                        SubjectName = "UA Core Sample Client"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "OPC Foundation/CertificateStores/UA Applications",
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "OPC Foundation/CertificateStores/UA Certificate Authorities",
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "OPC Foundation/CertificateStores/RejectedCertificates",
                    },
                    NonceLength = 32,
                    AutoAcceptUntrustedCertificates = true
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
            };

            await config.Validate(ApplicationType.Client);

            bool haveAppCertificate = config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

            if (!haveAppCertificate)
            {
                Console.WriteLine("    INFO: Creating new application certificate: {0}", config.ApplicationName);

                X509Certificate2 certificate = CertificateFactory.CreateCertificate(
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
                    config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
                }
            }
            else
            {
                Console.WriteLine("    WARN: missing application certificate, using unsecure connection.");
            }

            Console.WriteLine("2 - Discover endpoints of {0}.", endpointURL);
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointURL, haveAppCertificate);
            Console.WriteLine("    Selected endpoint uses: {0}",
                selectedEndpoint.SecurityPolicyUri.Substring(selectedEndpoint.SecurityPolicyUri.LastIndexOf('#') + 1));

            Console.WriteLine("3 - Create a session with OPC UA server.");
            var endpointConfiguration = EndpointConfiguration.Create(config);
            var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
            var session = await Session.Create(config, endpoint, true, ".Net Core OPC UA Console Client", 60000, new UserIdentity(new AnonymousIdentityToken()), null);

            Console.WriteLine("4 - Browse the OPC UA server namespace.");
            ReferenceDescriptionCollection references;
            Byte[] continuationPoint;

            references = session.FetchReferences(ObjectIds.ObjectsFolder);

            session.Browse(
                null,
                null,
                ObjectIds.ObjectsFolder,
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                out continuationPoint,
                out references);

            Console.WriteLine(" DisplayName, BrowseName, NodeClass");
            foreach (var rd in references)
            {
                Console.WriteLine(" {0}, {1}, {2}", rd.DisplayName, rd.BrowseName, rd.NodeClass);
                ReferenceDescriptionCollection nextRefs;
                byte[] nextCp;
                session.Browse(
                    null,
                    null,
                    ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris),
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                    out nextCp,
                    out nextRefs);

                foreach (var nextRd in nextRefs)
                {
                    Console.WriteLine("   + {0}, {1}, {2}", nextRd.DisplayName, nextRd.BrowseName, nextRd.NodeClass);
                }
            }

            Console.WriteLine("5 - Create a subscription with publishing interval of 1 second.");
            var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000 };

            Console.WriteLine("6 - Add a list of items (server current time and status) to the subscription.");
            var list = new List<MonitoredItem> {
                new MonitoredItem(subscription.DefaultItem)
                {
                    DisplayName = "ServerStatusCurrentTime", StartNodeId = "i=2258"
                }
            };
            list.ForEach(i => i.Notification += OnNotification);
            subscription.AddItems(list);

            Console.WriteLine("7 - Add the subscription to the session.");
            session.AddSubscription(subscription);
            subscription.Create();

            Console.WriteLine("8 - Monitoring for 10 seconds...");
            await Task.Delay(TimeSpan.FromSeconds(10));
            session.Close();
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
        /// This class contains the configuration for this module.
        /// </summary>
        class ModuleConfig
        {
            public bool AutoCall { get; }
            public ModuleConfig(bool autoCall)
            {
                this.AutoCall = autoCall;
            }
        }


        const string AutoCallKey = "AutoCall";
        /// <summary>
        /// Get the configuration for the module (in this case the threshold temperature)s.
        /// </summary>
        static async Task<ModuleConfig> GetConfiguration(DeviceClient deviceClient)
        {
            // First try to get the config from the Module twin
            Twin twin = await deviceClient.GetTwinAsync();
            if (twin.Properties.Desired.Contains(AutoCallKey))
            {
                bool autoCall = (bool)twin.Properties.Desired[AutoCallKey];
                return new ModuleConfig(autoCall);
            }

            return null;
        }
    }
}
