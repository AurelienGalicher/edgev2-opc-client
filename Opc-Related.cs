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

    public partial class Module
    {
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
        private async Task<Session> ConnectToServer(string endpointUrl)
        {
            var config = await CreateConfiguration();
            var selectedEndpoint = DiscoverEndpoints(endpointUrl, config);
            var session = await CreateSession(config, selectedEndpoint);

            return session;
        }

        private async Task<ApplicationConfiguration> CreateConfiguration()
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
    }
}