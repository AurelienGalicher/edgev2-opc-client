namespace EdgeOpcUAClient
{
    public class ModuleConfig
    {
        public string OpcUAConnectionString { get; }
        public string OpcUASampleValue { get; set; }
        public ModuleConfig(string opcUAConnectionString)
        {
            OpcUAConnectionString = opcUAConnectionString;
        }

        public override string ToString()
        {
            return $"{nameof(OpcUAConnectionString)}: {OpcUAConnectionString}, {nameof(OpcUASampleValue)}: {OpcUASampleValue}";
        }
    }
}