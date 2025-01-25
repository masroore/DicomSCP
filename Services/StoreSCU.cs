using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;

namespace DicomSCP.Services;

public interface IStoreSCU
{
    Task<bool> VerifyConnectionAsync(string remoteName, CancellationToken cancellationToken = default);
    Task SendFolderAsync(string folderPath, string remoteName, CancellationToken cancellationToken = default);
    Task SendFilesAsync(IEnumerable<string> filePaths, string remoteName, CancellationToken cancellationToken = default);
    Task SendFileAsync(string filePath, string remoteName, CancellationToken cancellationToken = default);
}

public class StoreSCU : IStoreSCU
{
    private readonly QueryRetrieveConfig _config;
    private readonly string _callingAE;

    public StoreSCU(IOptions<QueryRetrieveConfig> config)
    {
        _config = config.Value;
        _callingAE = _config.LocalAeTitle;
    }

    private IDicomClient CreateClient(RemoteNode node)
    {
        var client = DicomClientFactory.Create(
            node.HostName,
            node.Port,
            false,
            _callingAE,
            node.AeTitle);

        client.NegotiateAsyncOps();
        return client;
    }

    private RemoteNode GetRemoteNode(string remoteName)
    {
        var node = _config.RemoteNodes?.FirstOrDefault(n => n.Name.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
        if (node == null)
        {
            throw new ArgumentException($"Remote node configuration named {remoteName} not found");
        }
        return node;
    }

    public async Task SendFileAsync(string filePath, string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("StoreSCU", "Starting to send DICOM file - File: {FilePath}, Target: {Name} ({Host}:{Port} {AE})",
                filePath, node.Name, node.HostName, node.Port, node.AeTitle);

            // Create client
            var client = CreateClient(node);

            // Load DICOM file
            var file = await DicomFile.OpenAsync(filePath);

            // Create C-STORE request
            var request = new DicomCStoreRequest(file);
            var success = true;
            var errorMessage = string.Empty;

            // Register callback
            request.OnResponseReceived += (req, response) =>
            {
                if (response.Status.State != DicomState.Success)
                {
                    success = false;
                    errorMessage = $"{response.Status.State} [{response.Status.Code}: {response.Status.Description}]";
                    DicomLogger.Error("StoreSCU",
                        "Storage failed - File: {FilePath}, Status: {Status} [{Code}: {Description}]",
                        filePath,
                        response.Status.State,
                        response.Status.Code,
                        response.Status.Description);
                }
                else
                {
                    DicomLogger.Information("StoreSCU",
                        "Storage successful - File: {FilePath}, Status: {Status}",
                        filePath,
                        response.Status.State);
                }
            };

            // Send request
            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            if (!success)
            {
                throw new DicomNetworkException($"Storage failed: {errorMessage}");
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "Failed to send DICOM file - File: {FilePath}", filePath);
            throw;
        }
    }

    public async Task SendFilesAsync(IEnumerable<string> filePaths, string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("StoreSCU", "Starting to send multiple DICOM files - File count: {Count}, Target: {Name} ({Host}:{Port} {AE})",
                filePaths.Count(), node.Name, node.HostName, node.Port, node.AeTitle);

            // Create client
            var client = CreateClient(node);
            var failedFiles = new List<(string FilePath, string Error)>();

            // Add requests for all files
            foreach (var filePath in filePaths)
            {
                try
                {
                    var file = await DicomFile.OpenAsync(filePath);
                    var request = new DicomCStoreRequest(file);

                    request.OnResponseReceived += (req, response) =>
                    {
                        // Check if it is a duplicate instance status code (273 = 0x0111)
                        if (response.Status.Code == 273)  // Duplicate SOP Instance
                        {
                            DicomLogger.Warning("StoreSCU",
                                "Image already exists - File: {FilePath}", filePath);
                            // Do not add duplicate instances to the failed list
                        }
                        else if (response.Status.State != DicomState.Success)
                        {
                            var errorMessage = $"{response.Status.State} [{response.Status.Code}: {response.Status.Description}]";
                            DicomLogger.Error("StoreSCU",
                                "Storage failed - File: {FilePath}, Status: {Status}",
                                filePath, errorMessage);
                            failedFiles.Add((filePath, errorMessage));
                        }
                        else
                        {
                            DicomLogger.Information("StoreSCU",
                                "Storage successful - File: {FilePath}", filePath);
                        }
                    };

                    await client.AddRequestAsync(request);
                }
                catch (Exception ex)
                {
                    DicomLogger.Error("StoreSCU", ex, "Failed to add file to send queue - File: {FilePath}", filePath);
                    failedFiles.Add((filePath, ex.Message));
                    continue;
                }
            }

            // Send all requests
            await client.SendAsync(cancellationToken);

            // Check if there are any failed files
            if (failedFiles.Any())
            {
                var errorMessage = string.Join("\n", failedFiles.Select(f => $"File: {f.FilePath}, Error: {f.Error}"));
                throw new DicomNetworkException($"Some files failed to send:\n{errorMessage}");
            }

            DicomLogger.Information("StoreSCU", "Batch DICOM file sending completed - Total files: {Count}", filePaths.Count());
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "Failed to send multiple DICOM files");
            throw;
        }
    }

    public async Task SendFolderAsync(string folderPath, string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"Folder does not exist: {folderPath}");
            }

            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => IsDicomFile(f));

            await SendFilesAsync(files, remoteName, cancellationToken);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "Failed to send folder - Folder: {FolderPath}", folderPath);
            throw;
        }
    }

    public async Task<bool> VerifyConnectionAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("StoreSCU", "Starting connection verification - Target: {Name} ({Host}:{Port} {AE})",
                node.Name, node.HostName, node.Port, node.AeTitle);

            var client = CreateClient(node);

            var verified = false;
            var request = new DicomCEchoRequest();

            request.OnResponseReceived += (req, response) =>
            {
                verified = response.Status == DicomStatus.Success;
                DicomLogger.Information("StoreSCU", "Received C-ECHO response - Status: {Status}", response.Status);
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            DicomLogger.Information("StoreSCU", "Connection verification completed - Result: {Result}", verified);
            return verified;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StoreSCU", ex, "Connection verification failed");
            return false;
        }
    }

    private bool IsDicomFile(string filePath)
    {
        try
        {
            using var fileStream = File.OpenRead(filePath);
            var buffer = new byte[132];
            if (fileStream.Read(buffer, 0, 132) < 132)
            {
                return false;
            }

            // Check DICOM file header
            return buffer[128] == 'D' &&
                   buffer[129] == 'I' &&
                   buffer[130] == 'C' &&
                   buffer[131] == 'M';
        }
        catch
        {
            return false;
        }
    }
}
