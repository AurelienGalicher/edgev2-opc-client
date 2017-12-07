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

        // here you have to implement the message conversion for the OPC device.
        private async Task<MessageResponse> SendDataToDevice(DeviceClient deviceClient, Session session, Message message, ModuleConfig moduleConfig)
        {
            return await Task.Run( () => {
                byte[] messageBytes = message.GetBytes();
                string messageString = Encoding.UTF8.GetString(messageBytes);
                Console.WriteLine($"Message received: {messageString}");

                // send it to the OPC-UA device using the session.
                // session.Write(null, somevalcol, out _, out _);

                return MessageResponse.Completed;
            });
        }

        // check for module twin updates. Below is a sample value check
        private async Task UpdateDesiredProperties(TwinCollection desiredproperties, object usercontext, ModuleConfig moduleConfig)
        {
            if (desiredproperties.Contains(OpcUASampleValueKey))
            {
                string value = desiredproperties[OpcUASampleValueKey].ToString();
                if(!string.IsNullOrEmpty(value))
                {
                    moduleConfig.OpcUASampleValue = value;
                }
                Console.WriteLine($"{nameof(ModuleConfig.OpcUASampleValue)}: { moduleConfig.OpcUASampleValue}");
            }

            if (usercontext is DeviceClient deviceClient)
            {
                await deviceClient.UpdateReportedPropertiesAsync(CreateTwinCollectionFromModuleConfig(moduleConfig));
            }
        }


        /// <summary>
        /// Get the configuration for the module (in this case the OPC-UA Connection String for the Device).
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
