namespace DicomSCP.Configuration
{
    // 只包含允许编辑的配置项
    public class EditableSettings
    {
        // 基础配置
        public string? AeTitle { get; set; }
        public int StoreSCPPort { get; set; }
        public string? StoragePath { get; set; }
        public string? TempPath { get; set; }

        // 高级配置
        public AdvancedSettings Advanced { get; set; } = new();
        public class AdvancedSettings
        {
            public bool ValidateCallingAE { get; set; }
            public List<string> AllowedCallingAEs { get; set; } = new();
            public int ConcurrentStoreLimit { get; set; }
            public bool EnableCompression { get; set; }
            public string? PreferredTransferSyntax { get; set; }
        }

        // WorklistSCP配置
        public WorklistSettings WorklistSCP { get; set; } = new();
        public class WorklistSettings
        {
            public string? AeTitle { get; set; }
            public int Port { get; set; }
            public bool ValidateCallingAE { get; set; }
            public List<string> AllowedCallingAEs { get; set; } = new();
        }

        // QRSCP配置
        public QRSettings QRSCP { get; set; } = new();
        public class QRSettings
        {
            public string? AeTitle { get; set; }
            public int Port { get; set; }
            public bool ValidateCallingAE { get; set; }
            public List<string> AllowedCallingAETitles { get; set; } = new();
            public bool EnableCGet { get; set; }
            public bool EnableCMove { get; set; }
            public List<MoveDestination> MoveDestinations { get; set; } = new();
        }

        public class MoveDestination
        {
            public string? Name { get; set; }
            public string? AeTitle { get; set; }
            public string? HostName { get; set; }
            public int Port { get; set; }
            public bool IsDefault { get; set; }
        }

        // 查询检索配置
        public QueryRetrieveSettings QueryRetrieve { get; set; } = new();
        public class QueryRetrieveSettings
        {
            public string? LocalAeTitle { get; set; }
            public int LocalPort { get; set; }
            public List<RemoteNode> RemoteNodes { get; set; } = new();
        }
    }
} 