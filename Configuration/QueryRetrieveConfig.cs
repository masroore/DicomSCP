namespace DicomSCP.Configuration;

public class QueryRetrieveConfig
{
    public string LocalAeTitle { get; set; } = "QUERYSCU";
    public int LocalPort { get; set; } = 11114;
    public List<RemoteNode> RemoteNodes { get; set; } = new();
}

public class RemoteNode
{
    public string Name { get; set; } = string.Empty;
    public string AeTitle { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 104;
    public bool IsDefault { get; set; }
} 