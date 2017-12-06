namespace EdgeOpcUAClient
{
    public class ModuleConfig
    {
        public string OpcUAConnectionString { get; }
        public ModuleConfig(string opcUAConnectionString)
        {
            OpcUAConnectionString = opcUAConnectionString;
        }

        public override string ToString()
        {
            return $"{nameof(OpcUAConnectionString)}: {OpcUAConnectionString}";
        }
    }
}