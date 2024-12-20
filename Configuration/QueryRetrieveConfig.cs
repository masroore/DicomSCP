using System.ComponentModel.DataAnnotations;

namespace DicomSCP.Configuration;

public class QueryRetrieveConfig
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string LocalAeTitle { get; set; } = "QRSCU";

    [Range(1, 65535)]
    public int LocalPort { get; set; } = 11116;

    public List<RemoteNode> RemoteNodes { get; set; } = new();
}

public class RemoteNode
{
    public string Name { get; set; } = string.Empty;
    public string AeTitle { get; set; } = string.Empty;
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 104;
    public string Type { get; set; } = "all";

    public bool SupportsStore()
    {
        return Type.ToLower() is "store" or "all";
    }

    public bool SupportsQueryRetrieve()
    {
        return Type.ToLower() is "qr" or "all";
    }
} 