using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;

namespace DicomSCP.Services;

public interface IQueryRetrieveSCU
{
    Task<IEnumerable<DicomDataset>> QueryAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset query);
    Task<bool> MoveAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset dataset, string destinationAe, string? transferSyntax = null);
    Task<bool> VerifyConnectionAsync(RemoteNode node);
}

public class QueryRetrieveSCU : IQueryRetrieveSCU
{
    private readonly QueryRetrieveConfig _config;
    private readonly DicomSettings _settings;

    public QueryRetrieveSCU(
        IOptions<QueryRetrieveConfig> config,
        IOptions<DicomSettings> settings)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
    }

    private IDicomClient CreateClient(RemoteNode node)
    {
        DicomLogger.Debug("QueryRetrieveSCU", "Creating DICOM client connection - LocalAE: {LocalAE}, RemoteAE: {RemoteAE}, Host: {Host}:{Port}",
            _config.LocalAeTitle, node.AeTitle, node.HostName, node.Port);

        return DicomClientFactory.Create(
            node.HostName,
            node.Port,
            false,
            _config.LocalAeTitle,
            node.AeTitle);
    }

    public async Task<bool> MoveAsync(
        RemoteNode node,
        DicomQueryRetrieveLevel level,
        DicomDataset dataset,
        string destinationAe,
        string? transferSyntax = null)
    {
        try
        {
            // Validate if the destination AE is the local StoreSCP
            if (destinationAe != _settings.AeTitle)
            {
                DicomLogger.Error("QueryRetrieveSCU",
                    "Destination AE is not the local StoreSCP - DestAE: {DestAE}, LocalAE: {LocalAE}",
                    destinationAe, _settings.AeTitle);
                return false;
            }

            var client = CreateClient(node);

            DicomLogger.Information("QueryRetrieveSCU",
                "Starting {Level} level C-MOVE - SourceAET: {SourceAet}, DestinationAET: {DestinationAe}, TransferSyntax: {TransferSyntax}",
                level, node.AeTitle, destinationAe, transferSyntax ?? "default");

            // Add query level
            dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, level.ToString().ToUpper());

            // Get StudyInstanceUID
            var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");

            // Create C-MOVE request
            var request = new DicomCMoveRequest(destinationAe, studyUid);
            request.Dataset = dataset;

            // If transfer syntax is specified, configure the client's transfer syntax
            if (!string.IsNullOrEmpty(transferSyntax))
            {
                try
                {
                    // Get appropriate SOP Class UID
                    var sopClassUid = GetAppropriateSOPClassUID(level, dataset);

                    // Configure client's transfer syntax
                    client.AdditionalPresentationContexts.Clear();

                    // Create transfer syntax array
                    var transferSyntaxes = new[] { DicomTransferSyntax.Parse(transferSyntax) };

                    // Create presentation context with all required parameters
                    var presentationContext = new DicomPresentationContext(
                        1,  // presentation context ID
                        sopClassUid,  // abstract syntax (SOP Class UID)
                        true,  // provider role (SCU)
                        false  // user role (not SCP)
                    );

                    // Add transfer syntax
                    foreach (var syntax in transferSyntaxes)
                    {
                        presentationContext.AddTransferSyntax(syntax);
                    }

                    client.AdditionalPresentationContexts.Add(presentationContext);

                    DicomLogger.Debug("QueryRetrieveSCU",
                        "Transfer syntax set - SOP Class: {SopClass}, TransferSyntax: {TransferSyntax}",
                        sopClassUid.Name, transferSyntax);
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QueryRetrieveSCU", ex,
                        "Failed to set transfer syntax, using default - TransferSyntax: {TransferSyntax}",
                        transferSyntax);
                }
            }

            request.OnResponseReceived += (req, response) =>
            {
                if (response.Status == DicomStatus.Pending)
                {
                    DicomLogger.Debug("QueryRetrieveSCU",
                        "{Level} level C-MOVE in progress - TransferSyntax: {TransferSyntax}",
                        level, transferSyntax ?? "default");
                }
                else if (response.Status.State == DicomState.Failure)
                {
                    DicomLogger.Warning("QueryRetrieveSCU",
                        "{Level} level C-MOVE failed - {Error}, TransferSyntax: {TransferSyntax}",
                        level, response.Status.ErrorComment, transferSyntax ?? "default");
                }
            };

            await client.AddRequestAsync(request);
            // Send asynchronously, do not wait for completion
            _ = Task.Run(async () => await client.SendAsync());

            return true;  // Return immediately
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex,
                "{Level} level C-MOVE failed - TransferSyntax: {TransferSyntax}",
                level, transferSyntax ?? "default");
            throw;
        }
    }

    // Add helper method to get appropriate SOP Class UID
    private DicomUID GetAppropriateSOPClassUID(DicomQueryRetrieveLevel level, DicomDataset dataset)
    {
        // First try to get SOP Class UID from the dataset
        var sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
        if (!string.IsNullOrEmpty(sopClassUid))
        {
            return DicomUID.Parse(sopClassUid);
        }

        // If no SOP Class UID in the dataset, return appropriate storage SOP Class based on query level
        switch (level)
        {
            case DicomQueryRetrieveLevel.Patient:
            case DicomQueryRetrieveLevel.Study:
                // For Patient and Study levels, use general Study Root Query/Retrieve Information Model
                return DicomUID.StudyRootQueryRetrieveInformationModelMove;

            case DicomQueryRetrieveLevel.Series:
            case DicomQueryRetrieveLevel.Image:
                // Try to get modality information from the dataset
                var modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
                switch (modality.ToUpper())
                {
                    case "CT":
                        return DicomUID.CTImageStorage;
                    case "MR":
                        return DicomUID.MRImageStorage;
                    case "US":
                        return DicomUID.UltrasoundImageStorage;
                    case "XA":
                        return DicomUID.XRayAngiographicImageStorage;
                    case "CR":
                        return DicomUID.ComputedRadiographyImageStorage;
                    case "DX":
                        return DicomUID.DigitalXRayImageStorageForPresentation;
                    case "SC":
                        return DicomUID.SecondaryCaptureImageStorage;
                    default:
                        // If unable to determine specific type, use general Study Root Query/Retrieve Information Model
                        return DicomUID.StudyRootQueryRetrieveInformationModelMove;
                }

            default:
                return DicomUID.StudyRootQueryRetrieveInformationModelMove;
        }
    }

    public async Task<IEnumerable<DicomDataset>> QueryAsync(RemoteNode node, DicomQueryRetrieveLevel level, DicomDataset query)
    {
        var results = new List<DicomDataset>();
        var client = CreateClient(node);

        DicomLogger.Information("QueryRetrieveSCU",
            "Starting {Level} level C-FIND query - AET: {AeTitle}, Host: {Host}:{Port}",
            level, node.AeTitle, node.HostName, node.Port);

        var startTime = DateTime.Now;  // Add timing

        var request = new DicomCFindRequest(level);
        request.Dataset = query;

        request.OnResponseReceived += (request, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.HasDataset)
            {
                results.Add(response.Dataset);
                // Log time for each response
                var elapsed = DateTime.Now - startTime;
                DicomLogger.Debug("QueryRetrieveSCU",
                    "Received {Count}th response - Elapsed: {Elapsed}ms",
                    results.Count, elapsed.TotalMilliseconds);
                LogQueryResult(level, response.Dataset);
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync();

            var totalTime = DateTime.Now - startTime;
            DicomLogger.Information("QueryRetrieveSCU",
                "{Level} query completed - Found {Count} results, Total time: {Elapsed}ms",
                level, results.Count, totalTime.TotalMilliseconds);

            return results;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex,
                "Error occurred during {Level} query", level);
            throw;
        }
    }

    private void LogQueryResult(DicomQueryRetrieveLevel level, DicomDataset dataset)
    {
        switch (level)
        {
            case DicomQueryRetrieveLevel.Patient:
                DicomLogger.Debug("QueryRetrieveSCU",
                    "Received Patient query result - PatientId: {PatientId}, PatientName: {PatientName}",
                    dataset.GetSingleValueOrDefault(DicomTag.PatientID, "(no id)"),
                    dataset.GetSingleValueOrDefault(DicomTag.PatientName, "(no name)"));
                break;
            case DicomQueryRetrieveLevel.Study:
                DicomLogger.Debug("QueryRetrieveSCU",
                    "Received Study query result - PatientId: {PatientId}, StudyInstanceUid: {StudyUid}",
                    dataset.GetSingleValueOrDefault(DicomTag.PatientID, "(no id)"),
                    dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no uid)"));
                break;
            case DicomQueryRetrieveLevel.Series:
                DicomLogger.Debug("QueryRetrieveSCU",
                    "Received Series query result - StudyInstanceUid: {StudyUid}, SeriesInstanceUid: {SeriesUid}",
                    dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no study uid)"),
                    dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no series uid)"));
                break;
            case DicomQueryRetrieveLevel.Image:
                DicomLogger.Debug("QueryRetrieveSCU",
                    "Received Image query result - StudyInstanceUid: {StudyUid}, SeriesInstanceUid: {SeriesUid}, SopInstanceUid: {SopUid}",
                    dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(no study uid)"),
                    dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no series uid)"),
                    dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "(no sop uid)"));
                break;
        }
    }

    public async Task<bool> VerifyConnectionAsync(RemoteNode node)
    {
        try
        {
            var client = CreateClient(node);

            DicomLogger.Information("QueryRetrieveSCU",
                "Starting DICOM connection test - LocalAE: {LocalAE}, RemoteAE: {RemoteAE}, Host: {Host}:{Port}",
                _config.LocalAeTitle, node.AeTitle, node.HostName, node.Port);

            // Create C-ECHO request
            var request = new DicomCEchoRequest();

            var success = true;
            request.OnResponseReceived += (req, response) => {
                if (response.Status.State != DicomState.Success)
                {
                    success = false;
                    DicomLogger.Warning("QueryRetrieveSCU",
                        "C-ECHO failed - Status: {Status}", response.Status);
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync();

            if (success)
            {
                DicomLogger.Information("QueryRetrieveSCU",
                    "DICOM connection test successful - RemoteAE: {RemoteAE}", node.AeTitle);
            }

            return success;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QueryRetrieveSCU", ex,
                "DICOM connection test failed - RemoteAE: {RemoteAE}", node.AeTitle);
            return false;
        }
    }
}
