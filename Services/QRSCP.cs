using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging.Codec;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;
using System.Collections.Concurrent;  // 添加这个引用

namespace DicomSCP.Services;

public class QRSCP : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider, IDicomCMoveProvider, IDicomCGetProvider, IDicomCStoreProvider
{
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes = new[]
    {
        DicomTransferSyntax.JPEGLSLossless,            // JPEG-LS Lossless
        DicomTransferSyntax.JPEG2000Lossless,          // JPEG 2000 Lossless
        DicomTransferSyntax.JPEGProcess14SV1,          // JPEG Lossless
        DicomTransferSyntax.RLELossless,               // RLE Lossless
        DicomTransferSyntax.JPEGLSNearLossless,        // JPEG-LS Near Lossless
        DicomTransferSyntax.JPEG2000Lossy,             // JPEG 2000 Lossy
        DicomTransferSyntax.ExplicitVRLittleEndian,    // Explicit Little Endian
        DicomTransferSyntax.ImplicitVRLittleEndian,    // Implicit Little Endian
        DicomTransferSyntax.ExplicitVRBigEndian        // Explicit Big Endian
    };

    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;
    private bool _associationReleaseLogged = false;
    private bool _transferSyntaxLogged = false;

    // 用于跟踪每个目标 AE 的发送任务
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDestinations = new();

    public QRSCP(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        try
        {
            _settings = DicomServiceProvider.GetRequiredService<IOptions<DicomSettings>>().Value;
            _repository = DicomServiceProvider.GetRequiredService<DicomRepository>();
            DicomLogger.Information("QRSCP", "QR service initialized successfully");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "QR service initialization failed");
            throw;
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("QRSCP", "Received association request - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                association.CalledAE, association.CallingAE);

            // Validate Called AE
            var calledAE = association.CalledAE;
            var expectedAE = _settings?.QRSCP.AeTitle ?? string.Empty;

            if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("QRSCP", "Rejecting incorrect Called AE: {CalledAE}, Expected: {ExpectedAE}",
                    calledAE, expectedAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            // Validate Calling AE
            if (string.IsNullOrEmpty(association.CallingAE))
            {
                DicomLogger.Warning("QRSCP", "Rejecting empty Calling AE");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            // Check AllowedCallingAEs only if validation is configured
            if (_settings?.QRSCP.ValidateCallingAE == true)
            {
                if (!_settings.QRSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
                {
                    DicomLogger.Warning("QRSCP", "Rejecting unauthorized Calling AE: {CallingAE}", association.CallingAE);
                    return SendAssociationRejectAsync(
                        DicomRejectResult.Permanent,
                        DicomRejectSource.ServiceUser,
                        DicomRejectReason.CallingAENotRecognized);
                }
            }

            DicomLogger.Information("QRSCP", "Validation passed - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                calledAE, association.CallingAE);

            var storageCount = 0;
            foreach (var pc in association.PresentationContexts)
            {
                // Check if the requested service is supported
                if (pc.AbstractSyntax == DicomUID.Verification ||                                // C-ECHO
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelFind || // C-FIND
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelFind || // C-FIND (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove || // C-MOVE
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove || // C-MOVE (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||  // C-GET
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet || // C-GET (Patient Root)
                    pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)             // Storage (for C-GET)
                {
                    // Log service acceptance
                    if (pc.AbstractSyntax.StorageCategory == DicomStorageCategory.None)
                    {
                        DicomLogger.Information("QRSCP", "Accepting service - AET: {CallingAE}, Service: {Service}",
                            association.CallingAE, pc.AbstractSyntax.Name);
                    }

                    // Select appropriate transfer syntax based on service type
                    if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                    {
                        // For storage services (needed for C-GET), accept all supported transfer syntaxes
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                    }
                    else if (pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove)
                    {
                        // For C-GET/C-MOVE services, accept both basic and image transfer syntaxes
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes.Concat(AcceptedTransferSyntaxes).Distinct().ToArray());
                        DicomLogger.Information("QRSCP", "Accepting transfer syntaxes for C-GET/C-MOVE service - Service: {Service}", pc.AbstractSyntax.Name);
                    }
                    else
                    {
                        // For other services, use basic transfer syntaxes
                        pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                        DicomLogger.Information("QRSCP", "Accepting transfer syntaxes for basic service - Service: {Service}", pc.AbstractSyntax.Name);
                    }
                }
                else
                {
                    DicomLogger.Warning("QRSCP", "Rejecting unsupported service - AET: {CallingAE}, Service: {Service}",
                        association.CallingAE, pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            // Log a summary if there are storage services
            if (storageCount > 0)
            {
                DicomLogger.Information("QRSCP", "Accepting storage services - AET: {CallingAE}, Supported storage count: {Count}",
                    association.CallingAE, storageCount);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Failed to process association request");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        if (!_associationReleaseLogged)
        {
            DicomLogger.Information("QRSCP", "Received association release request");
            _associationReleaseLogged = true;
        }
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("QRSCP", "Received abort request - Source: {Source}, Reason: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        try
        {
            if (exception != null)
            {
                DicomLogger.Error("QRSCP", exception, "Connection closed with error");
            }

            // Reset state
            _associationReleaseLogged = false;
            _transferSyntaxLogged = false;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Failed to handle connection close");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("QRSCP", "Received C-ECHO request - From: {CallingAE}", Association.CallingAE);
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        DicomLogger.Information("QRSCP", "Received C-FIND request - From: {CallingAE}, Level: {Level}",
            Association.CallingAE, request.Level);

        if (request.Level != DicomQueryRetrieveLevel.Study &&
            request.Level != DicomQueryRetrieveLevel.Series &&
            request.Level != DicomQueryRetrieveLevel.Image &&
            request.Level != DicomQueryRetrieveLevel.Patient)
        {
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        // Handle fo-dicom query
        var responses = request.Level switch
        {
            DicomQueryRetrieveLevel.Patient => await HandlePatientLevelFind(request),
            DicomQueryRetrieveLevel.Study => await HandleStudyLevelFind(request),
            DicomQueryRetrieveLevel.Series => await HandleSeriesLevelFind(request),
            DicomQueryRetrieveLevel.Image => await HandleImageLevelFind(request),
            _ => new List<DicomCFindResponse>()
        };

        foreach (var response in responses)
        {
            yield return response;
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }
    private async Task<List<DicomCFindResponse>> HandleStudyLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // Extract query parameters from the request
            var queryParams = ExtractStudyQueryParameters(request);

            DicomLogger.Information("QRSCP", "Study level query parameters - PatientId: {PatientId}, PatientName: {PatientName}, " +
                "AccessionNumber: {AccessionNumber}, Date range: {StartDate} - {EndDate}, Modality: {Modality}, StudyInstanceUid: {StudyInstanceUid}",
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange.StartDate,
                queryParams.DateRange.EndDate,
                queryParams.Modalities,
                queryParams.StudyInstanceUid);

            // Query data from the database
            var studies = await Task.Run(() => _repository.GetStudies(
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange,
                queryParams.Modalities,
                queryParams.StudyInstanceUid,
                0,     // offset
                1000   // Limit to 1000 records
            ));

            DicomLogger.Information("QRSCP", "Study level query results - Record count: {Count}", studies.Count);

            // Build responses
            foreach (var study in studies)
            {
                var response = CreateStudyResponse(request, study);
                responses.Add(response);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Study level query failed: {Message}", ex.Message);
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private record StudyQueryParameters(
        string PatientId,
        string PatientName,
        string AccessionNumber,
        (string StartDate, string EndDate) DateRange,
        string[] Modalities,
        string StudyInstanceUid);

    private StudyQueryParameters ExtractStudyQueryParameters(DicomCFindRequest request)
    {
        // Handle potentially null strings
        string ProcessValue(string? value) =>
            (value?.Replace("*", "")) ?? string.Empty;

        // Log original date value
        var studyDate = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty);
        var studyTime = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty);
        if (!string.IsNullOrEmpty(studyDate))
        {
            DicomLogger.Debug("QRSCP", "Query date: {Date}", studyDate);
        }

        // Handle date range
        (string StartDate, string EndDate) ProcessDateRange(string? dateValue)
        {
            if (string.IsNullOrEmpty(dateValue))
                return (string.Empty, string.Empty);  // Return empty to query all records

            // Remove potential VR and tag information
            var cleanDateValue = dateValue;
            if (dateValue.Contains("DA"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(dateValue, @"\d{8}");
                if (match.Success)
                {
                    cleanDateValue = match.Value;
                }
            }

            // Handle DICOM date range format
            if (cleanDateValue.Contains("-"))
            {
                var parts = cleanDateValue.Split('-');
                if (parts.Length == 2)
                {
                    var startDate = parts[0].Trim();
                    var endDate = parts[1].Trim();

                    // Handle open-ended ranges
                    if (string.IsNullOrEmpty(startDate))
                    {
                        startDate = "19000101";  // Use minimum date
                    }
                    if (string.IsNullOrEmpty(endDate))
                    {
                        endDate = "99991231";    // Use maximum date
                    }

                    return (startDate, endDate);
                }
            }

            // If it's a single date, start and end dates are the same
            return (cleanDateValue, cleanDateValue);
        }

        var dateRange = ProcessDateRange(studyDate);

        // Handle Modality list
        string[] ProcessModalities(DicomDataset dataset)
        {
            var modalities = new List<string>();

            // Try to get ModalitiesInStudy
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                try
                {
                    var modalityValues = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                    if (modalityValues != null && modalityValues.Length > 0)
                    {
                        modalities.AddRange(modalityValues.Where(m => !string.IsNullOrEmpty(m)));
                    }
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QRSCP", ex, "Failed to get ModalitiesInStudy");
                }
            }

            // Try to get single Modality
            var singleModality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
            if (!string.IsNullOrEmpty(singleModality) && !modalities.Contains(singleModality))
            {
                modalities.Add(singleModality);
            }

            return modalities.ToArray();
        }

        var parameters = new StudyQueryParameters(
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty)),
            dateRange,
            ProcessModalities(request.Dataset),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty)));

        return parameters;
    }

    private DicomCFindResponse CreateStudyResponse(DicomCFindRequest request, Study study)
    {
        var response = new DicomCFindResponse(request, DicomStatus.Pending);
        var dataset = new DicomDataset();

        // Get character set from the request, default to UTF-8 if not specified
        var requestedCharacterSet = request.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");

        // Set response character set based on the request
        switch (requestedCharacterSet.ToUpperInvariant())
        {
            case "ISO_IR 100":  // Latin1
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                break;
            case "GB18030":     // Simplified Chinese
                dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                break;
            case "ISO_IR 192":  // UTF-8
            default:
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                break;
        }

        AddCommonTags(dataset, request.Dataset);

        dataset.Add(DicomTag.StudyInstanceUID, study.StudyInstanceUid)
              .Add(DicomTag.StudyDate, study.StudyDate ?? string.Empty)
              .Add(DicomTag.StudyTime, study.StudyTime ?? string.Empty)
              .Add(DicomTag.PatientName, study.PatientName ?? string.Empty)
              .Add(DicomTag.PatientID, study.PatientId ?? string.Empty)
              .Add(DicomTag.PatientBirthDate, study.PatientBirthDate ?? string.Empty)
              .Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty)
              .Add(DicomTag.ModalitiesInStudy, study.Modality ?? string.Empty)
              .Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty)
              .Add(DicomTag.NumberOfStudyRelatedSeries, study.NumberOfStudyRelatedSeries.ToString())
              .Add(DicomTag.NumberOfStudyRelatedInstances, study.NumberOfStudyRelatedInstances.ToString());

        response.Dataset = dataset;
        return response;
    }
    private async Task<List<DicomCFindResponse>> HandleSeriesLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Series level query missing StudyInstanceUID");
            return responses;
        }

        // Use Task.Run to perform database query asynchronously
        var seriesList = await Task.Run(() => _repository.GetSeriesByStudyUid(studyInstanceUid));

        foreach (var series in seriesList)
        {
            var response = new DicomCFindResponse(request, DicomStatus.Pending);
            var dataset = new DicomDataset();

            // Add character set and other common tags
            AddCommonTags(dataset, request.Dataset);

            // Set necessary fields
            dataset.Add(DicomTag.StudyInstanceUID, series.StudyInstanceUid);
            dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
            dataset.Add(DicomTag.Modality, series.Modality ?? string.Empty);
            dataset.Add(DicomTag.SeriesNumber, series.SeriesNumber ?? string.Empty);
            dataset.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);
            dataset.Add(DicomTag.NumberOfSeriesRelatedInstances, series.NumberOfInstances);

            // Copy other query fields from the request (if not already present)
            foreach (var tag in request.Dataset.Select(x => x.Tag))
            {
                if (!dataset.Contains(tag) && request.Dataset.TryGetString(tag, out string value))
                {
                    dataset.Add(tag, value);
                }
            }

            response.Dataset = dataset;
            responses.Add(response);
        }

        DicomLogger.Information("QRSCP", "Series level query completed - Record count: {Count}", responses.Count);
        return responses;
    }

    private async Task<List<DicomCFindResponse>> HandleImageLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);

        if (string.IsNullOrEmpty(studyInstanceUid) || string.IsNullOrEmpty(seriesInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Image level query missing StudyInstanceUID or SeriesInstanceUID");
            return responses;
        }

        // Use Task.Run to perform database query asynchronously
        var instances = await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));

        foreach (var instance in instances)
        {
            try
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                // Add character set and other common tags
                AddCommonTags(dataset, request.Dataset);

                // Validate UID format
                var validStudyUid = ValidateUID(studyInstanceUid);
                var validSeriesUid = ValidateUID(seriesInstanceUid);
                var validSopInstanceUid = ValidateUID(instance.SopInstanceUid);
                var validSopClassUid = ValidateUID(instance.SopClassUid);

                // Set necessary fields
                dataset.Add(DicomTag.StudyInstanceUID, validStudyUid);
                dataset.Add(DicomTag.SeriesInstanceUID, validSeriesUid);
                dataset.Add(DicomTag.SOPInstanceUID, validSopInstanceUid);
                dataset.Add(DicomTag.SOPClassUID, validSopClassUid);
                dataset.Add(DicomTag.InstanceNumber, instance.InstanceNumber ?? string.Empty);

                response.Dataset = dataset;
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "Failed to create Image response - SOPInstanceUID: {SopInstanceUid}",
                    instance.SopInstanceUid);
                continue;
            }
        }

        DicomLogger.Information("QRSCP", "Image level query completed - Record count: {Count}", responses.Count);
        return responses;
    }

    private string ValidateUID(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;

        try
        {
            // Split UID
            var parts = uid.Split('.');
            var validParts = new List<string>();

            foreach (var part in parts)
            {
                // Skip empty parts
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                // Remove leading zeros and ensure at least one digit remains
                var trimmed = part.TrimStart('0');
                validParts.Add(string.IsNullOrEmpty(trimmed) ? "0" : trimmed);
            }

            // Ensure at least two components
            if (validParts.Count < 2)
            {
                DicomLogger.Warning("QRSCP", "Invalid UID format (insufficient components): {Uid}",
                    uid ?? string.Empty);
                return "0.0";
            }

            return string.Join(".", validParts);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "UID validation failed: {Uid}",
                uid ?? string.Empty);
            return "0.0";
        }
    }

    private void AddCommonTags(DicomDataset dataset, DicomDataset requestDataset)
    {
        // Let fo-dicom handle character set
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet,
            requestDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192"));
    }

    public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
    {
        var destinationAE = request.DestinationAE;

        // Check if the destination AE is already being processed
        if (_activeDestinations.TryGetValue(destinationAE, out var existingCts))
        {
            DicomLogger.Warning("QRSCP", "Destination AE has unfinished tasks, canceling old task - AET: {AET}", destinationAE);
            existingCts.Cancel();
            existingCts.Dispose();
            _activeDestinations.TryRemove(destinationAE, out _);
        }

        // Create a new cancellation token
        var cts = new CancellationTokenSource();
        _activeDestinations.TryAdd(destinationAE, cts);

        try
        {
            DicomLogger.Information("QRSCP", "Received C-MOVE request - AE: {CallingAE}, Destination: {DestinationAE}",
                Association.CallingAE, request.DestinationAE);

            // 1. Validate destination AE
            var moveDestination = _settings.QRSCP.MoveDestinations
                .FirstOrDefault(x => x.AeTitle.Equals(request.DestinationAE, StringComparison.OrdinalIgnoreCase));

            if (moveDestination == null)
            {
                DicomLogger.Warning("QRSCP", "Destination AE not configured - AET: {AET}", request.DestinationAE);
                yield return new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
                yield break;
            }

            // 2. Test destination SCP connection
            var client = CreateDicomClient(moveDestination);
            DicomResponse? response = null;
            try
            {
                var echoRequest = new DicomCEchoRequest();
                await client.AddRequestAsync(echoRequest);
                await client.SendAsync();
                DicomLogger.Information("QRSCP", "Destination SCP connection test successful - AET: {AET}", request.DestinationAE);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "Destination SCP connection test failed - AET: {AET}", request.DestinationAE);
                response = new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
            }

            if (response != null)
            {
                yield return (DicomCMoveResponse)response;
                yield break;
            }

            // 3. Get instance list
            var instances = await GetRequestedInstances(request);
            if (!instances.Any())
            {
                DicomLogger.Information("QRSCP", "No matching instances found");
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
                yield break;
            }

            // 4. Process instance transfer and return progress
            var totalInstances = instances.Count();
            var result = await ProcessInstances(request, instances, client, cts.Token);

            // 5. Return progress and final status
            yield return new DicomCMoveResponse(request, DicomStatus.Pending)
            {
                Dataset = CreateProgressResponse(
                    request,
                    totalInstances,
                    result.successCount,
                    result.failedCount).Dataset
            };

            var finalStatus = result.hasNetworkError ? DicomStatus.QueryRetrieveMoveDestinationUnknown :
                             result.failedCount > 0 ? DicomStatus.ProcessingFailure :
                             DicomStatus.Success;

            yield return new DicomCMoveResponse(request, finalStatus)
            {
                Dataset = CreateProgressResponse(
                    request,
                    totalInstances,
                    result.successCount,
                    result.failedCount,
                    finalStatus).Dataset
            };

            // Add completion log
            DicomLogger.Information("QRSCP",
                "C-MOVE completed - Total: {Total}, Success: {Success}, Failed: {Failed}, Status: {Status}",
                totalInstances,
                result.successCount,
                result.failedCount,
                finalStatus);
        }
        finally
        {
            // Clean up resources
            if (_activeDestinations.TryRemove(destinationAE, out var currentCts) && currentCts == cts)
            {
                cts.Dispose();
            }
        }
    }

    private async Task<(int successCount, int failedCount, bool hasNetworkError)> SendBatch(
        IDicomClient client,
        List<DicomFile> files,
        int currentSuccess,
        int currentFailed)
    {
        try
        {
            // Batch transcode
            var requestedTransferSyntax = GetRequestedTransferSyntax(files[0]);
            if (requestedTransferSyntax != null)
            {
                files = TranscodeFilesIfNeeded(files, requestedTransferSyntax);
            }

            await SendToDestinationAsync(client, files);
            return (currentSuccess, currentFailed, false);
        }
        catch (DicomNetworkException ex)
        {
            DicomLogger.Error("QRSCP", ex, "Network error, stopping send");
            return (currentSuccess, currentFailed + files.Count, true);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Send failed");
            return (currentSuccess, currentFailed + files.Count, false);
        }
    }

    private DicomResponse CreateProgressResponse(
        DicomRequest request,
        int totalInstances,
        int successCount,
        int failedCount,
        DicomStatus? status = null)
    {
        var remaining = totalInstances - (successCount + failedCount);
        var currentStatus = status ?? (remaining > 0 ? DicomStatus.Pending :
            failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success);

        var dataset = new DicomDataset()
            .Add(DicomTag.NumberOfRemainingSuboperations, (ushort)remaining)
            .Add(DicomTag.NumberOfCompletedSuboperations, (ushort)successCount)
            .Add(DicomTag.NumberOfFailedSuboperations, (ushort)failedCount);

        return request switch
        {
            DicomCGetRequest getRequest => new DicomCGetResponse(getRequest, currentStatus) { Dataset = dataset },
            DicomCMoveRequest moveRequest => new DicomCMoveResponse(moveRequest, currentStatus) { Dataset = dataset },
            _ => throw new ArgumentException("Unsupported request type", nameof(request))
        };
    }

    private IDicomClient CreateDicomClient(MoveDestination destination)
    {
        var client = DicomClientFactory.Create(
            destination.HostName,
            destination.Port,
            false,
            _settings.QRSCP.AeTitle,
            destination.AeTitle);

        client.NegotiateAsyncOps();

        // Add all possible storage class PresentationContexts
        var storageUids = new DicomUID[]
        {
            DicomUID.CTImageStorage,
            DicomUID.MRImageStorage,
            DicomUID.UltrasoundImageStorage,
            DicomUID.SecondaryCaptureImageStorage,
            DicomUID.XRayAngiographicImageStorage,
            DicomUID.XRayRadiofluoroscopicImageStorage,
            DicomUID.DigitalXRayImageStorageForPresentation,
            DicomUID.DigitalMammographyXRayImageStorageForPresentation,
            DicomUID.UltrasoundMultiFrameImageStorage,
            DicomUID.EnhancedCTImageStorage,
            DicomUID.EnhancedMRImageStorage,
            DicomUID.EnhancedXAImageStorage,
            DicomUID.NuclearMedicineImageStorage,
            DicomUID.PositronEmissionTomographyImageStorage
        };

        byte pcid = 1;
        foreach (var uid in storageUids)
        {
            var pc = new DicomPresentationContext(pcid++, uid);
            pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
            client.AdditionalPresentationContexts.Add(pc);
        }

        return client;
    }

    public async IAsyncEnumerable<DicomCGetResponse> OnCGetRequestAsync(DicomCGetRequest request)
    {
        DicomLogger.Information("QRSCP", "Received C-GET request - AE: {CallingAE}, Level: {Level}",
            Association.CallingAE, request.Level);

        // 1. Validate request parameters
        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "C-GET missing StudyInstanceUID");
            yield return new DicomCGetResponse(request, DicomStatus.InvalidAttributeValue);
            yield break;
        }

        // 2. Get instance list
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            DicomLogger.Information("QRSCP", "No matching instances found");
            yield return new DicomCGetResponse(request, DicomStatus.Success);
            yield break;
        }

        // 3. Process instance transfer
        var totalInstances = instances.Count();
        var result = await ProcessInstances(request, instances);

        // 4. Return only final status
        var finalStatus = result.hasNetworkError ? DicomStatus.ProcessingFailure :
                         result.failedCount > 0 ? DicomStatus.ProcessingFailure :
                         DicomStatus.Success;

        yield return new DicomCGetResponse(request, finalStatus)
        {
            Dataset = CreateProgressResponse(
                request,
                totalInstances,
                result.successCount,
                result.failedCount,
                finalStatus).Dataset
        };
    }
    private async Task<IEnumerable<Instance>> GetRequestedInstances(DicomRequest request)
    {
        try
        {
            var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
            var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
            var sopInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty);

            DicomLogger.Information("QRSCP",
                "Getting instance list - Study: {StudyUid}, Series: {SeriesUid}, SOP: {SopUid}",
                studyInstanceUid, seriesInstanceUid, sopInstanceUid);

            if (!string.IsNullOrEmpty(sopInstanceUid))
            {
                var instance = await Task.Run(() => _repository.GetInstanceAsync(sopInstanceUid));
                return instance != null ? new[] { instance } : Array.Empty<Instance>();
            }

            if (!string.IsNullOrEmpty(seriesInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
            }

            if (!string.IsNullOrEmpty(studyInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesByStudyUid(studyInstanceUid));
            }

            DicomLogger.Warning("QRSCP", "Request missing necessary UID");
            return Array.Empty<Instance>();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Failed to get instance list");
            return Array.Empty<Instance>();
        }
    }

    private async Task<List<DicomCFindResponse>> HandlePatientLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // Extract query parameters from the request
            var patientId = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty);
            var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);

            DicomLogger.Information("QRSCP",
                "Patient level query - ID: {PatientId}, Name: {PatientName}",
                patientId, patientName);

            // Query patient data from the database
            var patients = await Task.Run(() => _repository.GetPatients(patientId, patientName));

            // Build responses
            foreach (var patient in patients)
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                AddCommonTags(dataset, request.Dataset);

                dataset.Add(DicomTag.PatientID, patient.PatientId ?? string.Empty)
                      .Add(DicomTag.PatientName, patient.PatientName ?? string.Empty)
                      .Add(DicomTag.PatientBirthDate, patient.PatientBirthDate ?? string.Empty)
                      .Add(DicomTag.PatientSex, patient.PatientSex ?? string.Empty)
                      .Add(DicomTag.NumberOfPatientRelatedStudies, patient.NumberOfStudies.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedSeries, patient.NumberOfSeries.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedInstances, patient.NumberOfInstances.ToString());

                response.Dataset = dataset;
                responses.Add(response);
            }

            DicomLogger.Information("QRSCP", "Patient level query completed - Record count: {Count}", responses.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Patient level query failed");
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }

    private bool IsNetworkError(Exception ex)
    {
        if (ex == null) return false;

        // Check if it is a network-related exception
        return ex is System.Net.Sockets.SocketException ||
               ex is System.Net.WebException ||
               ex is System.IO.IOException ||
               ex is DicomNetworkException ||
               (ex.InnerException != null && IsNetworkError(ex.InnerException));
    }

    private DicomTransferSyntax? GetRequestedTransferSyntax(DicomFile file)
    {
        try
        {
            // 1. Get transfer syntax from the Presentation Context of C-MOVE/C-GET service
            var moveContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                                   pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove);

            var moveTransferSyntax = moveContext?.AcceptedTransferSyntax;
            if (moveTransferSyntax != null)
            {
                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP",
                        "Transfer syntax check - Local file: {SourceSyntax}, Requested format: {TargetSyntax}",
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        moveTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return moveTransferSyntax;
            }

            // 2. Get transfer syntax corresponding to the SOP Class UID of the current file
            var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
            var storageContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == sopClassUid);

            var storageTransferSyntax = storageContext?.AcceptedTransferSyntax;
            if (storageTransferSyntax != null)
            {
                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP",
                        "Storage class transfer syntax check - Local file: {SourceSyntax}, Requested format: {TargetSyntax}",
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        storageTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return storageTransferSyntax;
            }

            // 3. Use default transfer syntax if none is specified
            DicomLogger.Information("QRSCP", "No transfer syntax specified, using default transfer syntax: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Failed to get requested transfer syntax, using default transfer syntax: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
    }

    // Implement IDicomCStoreProvider interface
    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        DicomLogger.Warning("QRSCP", "Direct storage service not supported - AET: {CallingAE}, SOPInstanceUID: {SopInstanceUid}",
            Association?.CallingAE ?? "Unknown",
            request.SOPInstanceUID?.ToString() ?? string.Empty);

        // Return unsupported status
        return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        DicomLogger.Warning("QRSCP", e, "Storage request exception - Temp file: {TempFile}",
            tempFileName ?? string.Empty);
        return Task.CompletedTask;
    }

    private List<DicomFile> TranscodeFilesIfNeeded(List<DicomFile> files, DicomTransferSyntax targetSyntax)
    {
        // 1. Quick check if transcoding is needed
        if (files.All(f => f.Dataset.InternalTransferSyntax == targetSyntax))
        {
            return files;
        }

        var transcoder = new DicomTranscoder(DicomTransferSyntax.ExplicitVRLittleEndian, targetSyntax);
        var results = new ConcurrentBag<DicomFile>();  // Use thread-safe collection

        // 2. Parallel transcoding
        Parallel.ForEach(files,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            file =>
        {
            try
            {
                if (file.Dataset.InternalTransferSyntax != targetSyntax)
                {
                    var transcodedFile = transcoder.Transcode(file);
                    results.Add(transcodedFile);
                }
                else
                {
                    results.Add(file);
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("QRSCP", ex, "Syntax conversion failed, using original format");
                results.Add(file);
            }
        });

        return results.ToList();
    }

    private const int BatchSize = 10;  // Fixed batch size

    private async Task<(int successCount, int failedCount, bool hasNetworkError)> ProcessInstances(
        DicomRequest request,
        IEnumerable<Instance> instances,
        IDicomClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var totalInstances = instances.Count();
        var successCount = 0;
        var failedCount = 0;
        var hasNetworkError = false;

        foreach (var seriesGroup in instances.GroupBy(x => x.SeriesInstanceUid))
        {
            // Check if canceled
            if (cancellationToken.IsCancellationRequested)
            {
                DicomLogger.Warning("QRSCP", "Task canceled, stopping processing");
                break;
            }

            var batchFiles = new List<DicomFile>();
            try
            {
                foreach (var instance in seriesGroup)
                {
                    // Stop processing if network error or canceled
                    if (hasNetworkError || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                        if (!File.Exists(filePath))
                        {
                            failedCount++;
                            continue;
                        }

                        var file = await DicomFile.OpenAsync(filePath);
                        batchFiles.Add(file);

                        // Process batch when batch size is reached
                        if (batchFiles.Count >= BatchSize)
                        {
                            // Check if canceled
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            // Send batch
                            var result = client != null
                                ? await SendBatch(client, batchFiles, successCount, failedCount)
                                : await SendLocalBatch(request, batchFiles, successCount, failedCount);

                            if (result.hasNetworkError)
                            {
                                hasNetworkError = true;
                                failedCount = result.failedCount;
                                break;
                            }

                            successCount += batchFiles.Count;
                            failedCount = result.failedCount;
                            batchFiles.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        DicomLogger.Error("QRSCP", ex, "Processing failed - UID: {SopInstanceUid}", instance.SopInstanceUid);
                    }
                }

                // Skip remaining files if network error or canceled
                if (!hasNetworkError && !cancellationToken.IsCancellationRequested && batchFiles.Any())
                {
                    // Send batch
                    var result = client != null
                        ? await SendBatch(client, batchFiles, successCount, failedCount)
                        : await SendLocalBatch(request, batchFiles, successCount, failedCount);

                    if (result.hasNetworkError)
                    {
                        hasNetworkError = true;
                        failedCount = result.failedCount;
                    }
                    else
                    {
                        successCount += batchFiles.Count;
                        failedCount = result.failedCount;
                    }
                }
            }
            finally
            {
                batchFiles.Clear();
            }

            // Stop processing next series if network error or canceled
            if (hasNetworkError || cancellationToken.IsCancellationRequested)
            {
                DicomLogger.Warning("QRSCP",
                    cancellationToken.IsCancellationRequested ? "Task canceled, stopping processing" : "Network error detected, stopping processing");
                break;
            }
        }

        return (successCount, failedCount, hasNetworkError);
    }

    // C-GET local send
    private async Task<(int successCount, int failedCount, bool hasNetworkError)> SendLocalBatch(
        DicomRequest request,
        List<DicomFile> files,
        int currentSuccess,
        int currentFailed)
    {
        try
        {
            // 1. Batch transcode
            var requestedTransferSyntax = GetRequestedTransferSyntax(files[0]);
            if (requestedTransferSyntax != null)
            {
                files = TranscodeFilesIfNeeded(files, requestedTransferSyntax);
            }

            // 2. Send one by one
            foreach (var file in files)
            {
                await SendRequestAsync(new DicomCStoreRequest(file));  // Use base class's SendRequestAsync
            }
            return (currentSuccess + files.Count, currentFailed, false);
        }
        catch (Exception ex)
        {
            if (IsNetworkError(ex))
            {
                DicomLogger.Error("QRSCP", ex, "Network error, stopping send");
                return (currentSuccess, currentFailed + files.Count, true);
            }

            DicomLogger.Error("QRSCP", ex, "Send failed");
            return (currentSuccess, currentFailed + files.Count, false);
        }
    }

    private async Task SendToDestinationAsync(IDicomClient client, List<DicomFile> files)
    {
        // 1. Add PresentationContext grouped by SOP Class
        var sopClassGroups = files.GroupBy(f => f.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID));
        foreach (var group in sopClassGroups)
        {
            var presentationContext = new DicomPresentationContext(
                (byte)client.AdditionalPresentationContexts.Count(),
                group.Key);
            presentationContext.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
            client.AdditionalPresentationContexts.Add(presentationContext);
        }

        // 2. Add requests in batch
        foreach (var file in files)
        {
            await client.AddRequestAsync(new DicomCStoreRequest(file));
        }

        // 3. Send all requests at once
        await client.SendAsync();
    }
}
