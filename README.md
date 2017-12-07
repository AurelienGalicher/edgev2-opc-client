# Azure IoT Edge OPC-UA Client Template Project

This project is a dotnetcore template for Azure IoT Edge to create a basic OPC-UA Client.

## How to use this template

You can just clone this directory or `dotnet new -i DariuszParys.OPC.IoTEdgeModule.CSharp` to install the dotnet core project template

This project is using partial classes to split up the plumbing for connecting to the OPC device and the custom logic you implement.

- Opc-Relates.cs contains the infrastructure code for connecting to the device
- Module.cs contains the code to connect to the Edge message queues and custom logic would go here.

To have a simple scenario up and running this template is listening on incoming IoT Edge messages under `input1`. You can for instance use the Temperature Sensor from the Azure IoT Edge Tutorials to pump data into it. Further it is preconficured to talk with the sample opc-ua-server from this repository.

## Module Twin Properties

Out of the box the implementation is using two properties

`OpcUASampleValue` - This will just be logged out to the console
`OpcUAConnectionString` - This is the connection string for your device. If not specified it will use `opc.tcp://opc-ua-server:51210/UA/SampleServer`.

```json
{
    "propertiesDesired": {
        "OpcUASampleValue": "Some text...",
        "OpcUAConnectionString": "opc.tcp://opc-ua-server:51210/UA/SampleServer"
    }
}
```

## Sample deployment with prebaked containers

```json
{
  "moduleContent": {
    "$edgeAgent": {
      "properties.desired": {
        "schemaVersion": "1.0",
        "runtime": {
          "type": "docker",
          "settings": {
            "minDockerVersion": "v1.25",
            "loggingOptions": ""
          }
        },
        "systemModules": {
          "edgeAgent": {
            "type": "docker",
            "settings": {
              "image": "microsoft/azureiotedge-agent:1.0-preview",
              "createOptions": ""
            }
          },
          "edgeHub": {
            "type": "docker",
            "status": "running",
            "restartPolicy": "always",
            "settings": {
              "image": "microsoft/azureiotedge-hub:1.0-preview",
              "createOptions": ""
            }
          }
        },
        "modules": {
          "opc-client": {
            "version": "1.0",
            "type": "docker",
            "status": "running",
            "restartPolicy": "always",
            "settings": {
              "image": "dariuszparys/edge-opc-ua-client-linux-x64:latest",
              "createOptions": "{}"
            }
          },
          "tempSensor": {
            "version": "1.0",
            "type": "docker",
            "status": "running",
            "restartPolicy": "always",
            "settings": {
              "image": "microsoft/azureiotedge-simulated-temperature-sensor:1.0-preview",
              "createOptions": "{}"
            }
          },
          "opc-ua-server": {
            "version": "1.0",
            "type": "docker",
            "status": "running",
            "restartPolicy": "always",
            "settings": {
              "image": "dariuszparys/opc-ua-server-module-linux-x64:latest",
              "createOptions": "{\"HostConfig\":{\"PortBindings\":{\"4840/tcp\":[{\"HostPort\":\"4840\"}],\"51210/tcp\":[{\"HostPort\":\"51210\"}]}}}"
            }
          }
        }
      }
    },
    "$edgeHub": {
      "properties.desired": {
        "schemaVersion": "1.0",
        "routes": {
          "sensorToOpcClient": "FROM /messages/modules/tempSensor/outputs/temperatureOutput INTO BrokeredEndpoint(\"/modules/opc-client/inputs/input1\")"
        },
        "storeAndForwardConfiguration": {
          "timeToLiveSecs": 7200
        }
      }
    },
    "opc-client": {
      "properties.desired": {
        "OpcUASampleValue": "Some text to log",
        "OpcUAConnectionString": "opc.tcp://opc-ua-server:51210/UA/SampleServer"
      }
    },
    "tempSensor": {
      "properties.desired": {}
    },
    "opc-ua-server": {
      "properties.desired": {}
    }
  }
}
```