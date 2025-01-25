using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Data;
using DicomSCP.Models;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using TinyPinyin;
using System.Globalization;

namespace DicomSCP.Services;

public record WorklistQueryParameters(
    string PatientId,
    string PatientName,
    string AccessionNumber,
    (string StartDate, string EndDate) DateRange,
    string Modality,
    string ScheduledStationName);

public class WorklistSCP : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
{
    private static DicomSettings? _settings;
    private static DicomRepository? _repository;

    public static void Configure(
        DicomSettings settings,
        IConfiguration configuration,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public WorklistSCP(
        INetworkStream stream,
        Encoding fallbackEncoding,
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies,
        IOptions<DicomSettings> settings)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        if (settings?.Value == null || dependencies?.LoggerFactory == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        _settings = settings.Value;
        DicomLogger.Information("Service initialized");
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("WorklistSCP", exception, "Connection closed with error");
        }
        else
        {
            DicomLogger.Debug("WorklistSCP", "Connection closed normally");
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        var calledAE = association.CalledAE;
        var expectedAE = _settings?.WorklistSCP.AeTitle ?? string.Empty;

        if (!string.Equals(expectedAE, calledAE, StringComparison.OrdinalIgnoreCase))
        {
            DicomLogger.Warning("WorklistSCP", "Rejecting incorrect Called AE: {CalledAE}, Expected: {ExpectedAE}",
                calledAE, expectedAE);
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CalledAENotRecognized);
        }

        if (string.IsNullOrEmpty(association.CallingAE))
        {
            DicomLogger.Warning("WorklistSCP", "Rejecting empty Calling AE");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.CallingAENotRecognized);
        }

        if (_settings?.WorklistSCP.ValidateCallingAE == true)
        {
            if (!_settings.WorklistSCP.AllowedCallingAEs.Contains(association.CallingAE, StringComparer.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("WorklistSCP", "Rejecting unauthorized Calling AE: {CallingAE}", association.CallingAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }
        }

        DicomLogger.Debug("WorklistSCP", "Validation passed - Called AE: {CalledAE}, Calling AE: {CallingAE}",
            calledAE, association.CallingAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind ||
                pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(
                    DicomTransferSyntax.ImplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRLittleEndian,
                    DicomTransferSyntax.ExplicitVRBigEndian);
                DicomLogger.Debug("WorklistSCP", "Accepting service - AET: {CallingAE}, Service: {Service}",
                    association.CallingAE, pc.AbstractSyntax.Name);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                DicomLogger.Warning("WorklistSCP", "Rejecting unsupported service - AET: {CallingAE}, AbstractSyntax: {AbstractSyntax}",
                    association.CallingAE, pc.AbstractSyntax);
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Debug("WorklistSCP", "Received association release request");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("WorklistSCP", "Received abort request - Source: {Source}, Reason: {Reason}", source, reason);
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        if (_settings == null)
        {
            DicomLogger.Error("WorklistSCP", null, "Service not configured");
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        DicomLogger.Debug("WorklistSCP", "Received worklist query request - Original dataset: {@Dataset}",
            request.Dataset.ToDictionary(x => x.Tag.ToString(), x => x.ToString()));

        var responses = await Task.Run(() => ProcessWorklistQuery(request));
        foreach (var response in responses)
        {
            yield return response;
        }
    }

    private IEnumerable<DicomCFindResponse> ProcessWorklistQuery(DicomCFindRequest request)
    {
        List<WorklistItem> worklistItems;
        try
        {
            var parameters = ExtractQueryParameters(request);

            worklistItems = _repository?.GetWorklistItems(
                parameters.PatientId,
                parameters.PatientName,
                parameters.AccessionNumber,
                parameters.DateRange,
                parameters.Modality,
                parameters.ScheduledStationName) ?? new List<WorklistItem>();

            DicomLogger.Information("WorklistSCP", "Found worklist items: {Count} records", worklistItems.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "Worklist query failed: {Message}", ex.Message);
            return new[] { new DicomCFindResponse(request, DicomStatus.ProcessingFailure) };
        }

        if (worklistItems.Count == 0)
        {
            DicomLogger.Debug("WorklistSCP", "No matching worklist items found");
            return new[] { new DicomCFindResponse(request, DicomStatus.Success) };
        }

        var responses = new List<DicomCFindResponse>();
        var hasErrors = false;

        foreach (var item in worklistItems)
        {
            try
            {
                var response = CreateWorklistResponse(request, item);
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WorklistSCP", ex, "Failed to create response - PatientId: {PatientId}", item.PatientId);
                hasErrors = true;
            }
        }

        if (responses.Count == 0 && hasErrors)
        {
            DicomLogger.Error("WorklistSCP", null, "All response creations failed");
            return new[] { new DicomCFindResponse(request, DicomStatus.ProcessingFailure) };
        }

        DicomLogger.Information("WorklistSCP", "Worklist query completed - Returned records: {Count}, Has errors: {HasErrors}",
            responses.Count, hasErrors);
        responses.Add(new DicomCFindResponse(request, DicomStatus.Success));
        return responses;
    }

    private List<WorklistItem> QueryWorklistItems(
        (string PatientId, string AccessionNumber, string ScheduledDateTime, string Modality, string ScheduledStationName) filters)
    {
        if (_repository == null)
        {
            DicomLogger.Error("WorklistSCP", null, "Repository not configured");
            throw new InvalidOperationException("Repository not configured");
        }

        try
        {
            DicomLogger.Debug("WorklistSCP", "Executing worklist query");
            return _repository.GetWorklistItems(
                filters.PatientId,
                string.Empty,
                filters.AccessionNumber,
                (filters.ScheduledDateTime, filters.ScheduledDateTime),
                filters.Modality,
                filters.ScheduledStationName);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "Worklist query failed - Filters: {@Filters}", filters);
            throw;
        }
    }

    private DicomCFindResponse CreateWorklistResponse(DicomCFindRequest request, WorklistItem item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        try
        {
            var dataset = new DicomDataset();

            // Get character set from request
            var requestedCharacterSet = "ISO_IR 100";  // Default value
            if (request.Dataset.Contains(DicomTag.SpecificCharacterSet))
            {
                var charsets = request.Dataset.GetValues<string>(DicomTag.SpecificCharacterSet);
                if (charsets != null && charsets.Length > 0)
                {
                    requestedCharacterSet = charsets[0];
                }
            }

            DicomLogger.Debug("WorklistSCP", "Requested character set: {CharacterSet}", requestedCharacterSet);

            // Determine if Chinese name conversion is needed
            bool needConvertName = true;
            string patientName = item.PatientName;

            // Set response character set based on request
            switch (requestedCharacterSet.ToUpperInvariant())
            {
                case "ISO_IR 100":  // Latin1
                    dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                    needConvertName = true;  // Latin1 does not support Chinese, conversion needed
                    break;
                case "GB18030":     // Simplified Chinese
                case "GBK":         // Simplified Chinese
                case "GB2312":      // Simplified Chinese
                    dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                    needConvertName = false;  // GB18030 supports Chinese, no conversion needed
                    break;
                case "ISO_IR 192":  // UTF-8
                    dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                    needConvertName = false;  // UTF-8 supports Chinese, no conversion needed
                    break;
                default:            // Other unknown character sets, use Latin1 as a safe option
                    dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                    needConvertName = true;  // Use Pinyin
                    DicomLogger.Warning("WorklistSCP", "Unknown character set: {CharacterSet}, using Latin1", requestedCharacterSet);
                    break;
            }

            // Determine if Chinese name conversion is needed based on character set
            if (needConvertName)
            {
                patientName = ConvertToDeviceName(item.PatientName);
                DicomLogger.Debug("WorklistSCP",
                    "Converted patient name - Original: {OriginalName}, Converted: {ConvertedName}, Character set: {CharacterSet}",
                    item.PatientName,
                    patientName,
                    requestedCharacterSet);
            }
            else
            {
                DicomLogger.Debug("WorklistSCP",
                    "Using original Chinese name - Patient name: {PatientName}, Character set: {CharacterSet}",
                    patientName,
                    requestedCharacterSet);
            }

            // Patient information
            dataset.Add(DicomTag.PatientID, ProcessDicomValue(item.PatientId, DicomTag.PatientID, needConvertName));
            dataset.Add(DicomTag.PatientName, ProcessDicomValue(patientName, DicomTag.PatientName, needConvertName));
            dataset.Add(DicomTag.PatientSex, ProcessDicomValue(item.PatientSex ?? "", DicomTag.PatientSex, needConvertName));

            // Ensure birth date format is correct
            try
            {
                if (!string.IsNullOrEmpty(item.PatientBirthDate))
                {
                    var birthDate = DateTime.ParseExact(item.PatientBirthDate, "yyyyMMdd", null);
                    dataset.Add(DicomTag.PatientBirthDate, birthDate.ToString("yyyyMMdd"));
                }
                else
                {
                    dataset.Add(DicomTag.PatientBirthDate, "19000101");  // Use default value
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("WorklistSCP",
                    "Failed to process birth date - PatientId: {PatientId}, BirthDate: {BirthDate}, Error: {Error}",
                    item.PatientId ?? "",
                    item.PatientBirthDate ?? "",
                    ex.Message);
                dataset.Add(DicomTag.PatientBirthDate, "19000101");  // Use default value
            }

            // Add age information
            if (!string.IsNullOrEmpty(item.PatientBirthDate))
            {
                try
                {
                    var birthDate = DateTime.ParseExact(item.PatientBirthDate, "yyyyMMdd", null);
                    var age = DateTime.Now.Year - birthDate.Year;
                    if (DateTime.Now.DayOfYear < birthDate.DayOfYear)
                    {
                        age--;
                    }
                    dataset.Add(DicomTag.PatientAge, $"{age:000}Y");  // Format as "045Y"
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("WorklistSCP",
                        "Failed to calculate age - PatientId: {PatientId}, BirthDate: {BirthDate}, Error: {Error}",
                        item.PatientId ?? "",
                        item.PatientBirthDate ?? "",
                        ex.Message);
                }
            }

            // Study information
            dataset.Add(DicomTag.StudyInstanceUID, ProcessDicomValue(item.StudyInstanceUid, DicomTag.StudyInstanceUID, needConvertName));
            dataset.Add(DicomTag.AccessionNumber, ProcessDicomValue(item.AccessionNumber, DicomTag.AccessionNumber, needConvertName));
            // Physician name also needs to be processed based on character set
            var physicianName = needConvertName ?
                ConvertToDeviceName(item.ReferringPhysicianName) :
                item.ReferringPhysicianName;
            dataset.Add(DicomTag.ReferringPhysicianName, ProcessDicomValue(physicianName, DicomTag.ReferringPhysicianName, needConvertName));

            // Scheduled information
            dataset.Add(DicomTag.Modality, ProcessDicomValue(item.Modality, DicomTag.Modality, needConvertName));
            dataset.Add(DicomTag.ScheduledStationAETitle, ProcessDicomValue(item.ScheduledAET, DicomTag.ScheduledStationAETitle, needConvertName));

            // Process scheduled date and time
            try
            {
                if (!string.IsNullOrEmpty(item.ScheduledDateTime))
                {
                    DateTime scheduledDateTime;
                    string dateStr = item.ScheduledDateTime.Trim();

                    // Remove all non-numeric characters
                    string numericOnly = new string(dateStr.Where(char.IsDigit).ToArray());

                    // Determine format based on numeric length
                    if (numericOnly.Length >= 8)
                    {
                        string formattedDate;
                        if (numericOnly.Length >= 12)
                        {
                            // Case with time included
                            formattedDate = numericOnly.Substring(0, 8) +
                                          (numericOnly.Length >= 12 ? numericOnly.Substring(8, 4) : "0000") +
                                          (numericOnly.Length >= 14 ? numericOnly.Substring(12, 2) : "00");
                        }
                        else
                        {
                            // Case with only date
                            formattedDate = numericOnly.Substring(0, 8) + "000000";
                        }

                        if (DateTime.TryParseExact(formattedDate,
                            "yyyyMMddHHmmss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out scheduledDateTime))
                        {
                            dataset.Add(DicomTag.ScheduledProcedureStepStartDate,
                                scheduledDateTime.ToString("yyyyMMdd"));
                            dataset.Add(DicomTag.ScheduledProcedureStepStartTime,
                                scheduledDateTime.ToString("HHmmss"));

                            DicomLogger.Debug("WorklistSCP",
                                "Scheduled time processed successfully - Original: {Original}, Formatted: {Formatted}, Converted date: {Date}, Time: {Time}",
                                item.ScheduledDateTime,
                                formattedDate,
                                scheduledDateTime.ToString("yyyyMMdd"),
                                scheduledDateTime.ToString("HHmmss"));
                        }
                        else
                        {
                            throw new FormatException($"Failed to parse date-time format: {formattedDate}");
                        }
                    }
                    else
                    {
                        throw new FormatException($"Date-time string length insufficient: {numericOnly}");
                    }
                }
                else
                {
                    // Use current time if no scheduled time is provided
                    var now = DateTime.Now;
                    dataset.Add(DicomTag.ScheduledProcedureStepStartDate, now.ToString("yyyyMMdd"));
                    dataset.Add(DicomTag.ScheduledProcedureStepStartTime, now.ToString("HHmmss"));
                    DicomLogger.Debug("WorklistSCP", "Using current time as scheduled time: {DateTime}",
                        now.ToString("yyyyMMddHHmmss"));
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("WorklistSCP",
                    "Failed to process scheduled time - PatientId: {PatientId}, DateTime: {DateTime}, Error: {Error}",
                    item.PatientId ?? "",
                    item.ScheduledDateTime ?? "",
                    ex.Message);
                // Use current time in case of error
                var now = DateTime.Now;
                dataset.Add(DicomTag.ScheduledProcedureStepStartDate, now.ToString("yyyyMMdd"));
                dataset.Add(DicomTag.ScheduledProcedureStepStartTime, now.ToString("HHmmss"));
            }

            dataset.Add(DicomTag.ScheduledStationName, ProcessDicomValue(item.ScheduledStationName, DicomTag.ScheduledStationName, needConvertName));
            dataset.Add(DicomTag.ScheduledProcedureStepID, ProcessDicomValue(item.ScheduledProcedureStepID, DicomTag.ScheduledProcedureStepID, needConvertName));
            dataset.Add(DicomTag.RequestedProcedureID, ProcessDicomValue(item.RequestedProcedureID, DicomTag.RequestedProcedureID, needConvertName));

            var response = new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = dataset };
            DicomLogger.Debug("WorklistSCP",
                "Successfully created response - PatientId: {PatientId}, AccessionNumber: {AccessionNumber}",
                item.PatientId ?? "",
                item.AccessionNumber ?? "");
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "Failed to create response dataset - PatientId: {PatientId}", item.PatientId);
            throw new DicomNetworkException("Failed to create worklist response", ex);
        }
    }

    private WorklistQueryParameters ExtractQueryParameters(DicomCFindRequest request)
    {
        // Log original request parameters
        DicomLogger.Debug("WorklistSCP", "Received query request: {@Tags}",
            request.Dataset.Where(x => !x.Tag.IsPrivate)
                         .ToDictionary(x => x.Tag.ToString(), x => x.ToString()));

        var modality = GetModality(request.Dataset);
        var dateRange = GetDateRange(request.Dataset);

        // Get patient name
        var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);
        DicomLogger.Debug("WorklistSCP", "Query patient name: {PatientName}", patientName);

        var parameters = new WorklistQueryParameters(
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty),
            patientName,
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            dateRange,
            modality,
            request.Dataset.GetSingleValueOrDefault<string>(DicomTag.ScheduledStationName, string.Empty)
        );

        DicomLogger.Debug("WorklistSCP", "Parsed query parameters - PatientId: {PatientId}, PatientName: {PatientName}, " +
            "AccessionNumber: {AccessionNumber}, Date range: {StartDate} - {EndDate}, Modality: {Modality}, StationName: {StationName}",
            parameters.PatientId,
            parameters.PatientName,
            parameters.AccessionNumber,
            parameters.DateRange.StartDate,
            parameters.DateRange.EndDate,
            parameters.Modality,
            parameters.ScheduledStationName);

        return parameters;
    }

    private string GetModality(DicomDataset dataset)
    {
        // First try to get from ScheduledProcedureStep Sequence
        var modality = string.Empty;
        if (dataset.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var stepSequence = dataset.GetSequence(DicomTag.ScheduledProcedureStepSequence);
            if (stepSequence.Items.Any())
            {
                modality = stepSequence.Items[0].GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
                if (!string.IsNullOrEmpty(modality))
                {
                    DicomLogger.Debug("WorklistSCP", "Got Modality from ScheduledProcedureStep: {Modality}", modality);
                    return modality;
                }
            }
        }

        // If not found, try to get from root level
        modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
        DicomLogger.Debug("WorklistSCP", "Got Modality from root level: {Modality}", modality);
        return modality;
    }

    private (string StartDate, string EndDate) GetDateRange(DicomDataset dataset)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        string startDate, endDate;

        if (dataset.Contains(DicomTag.ScheduledProcedureStepStartDate))
        {
            var dateElement = dataset.GetDicomItem<DicomElement>(DicomTag.ScheduledProcedureStepStartDate);
            var values = dateElement?.Get<string[]>() ?? Array.Empty<string>();

            if (values.Length > 0)
            {
                var validDates = values
                    .Where(v => !string.IsNullOrEmpty(v) && v.Length == 8)
                    .ToList();

                if (validDates.Any())
                {
                    startDate = validDates.Min() ?? today;
                    endDate = today;
                    var validDatesStr = string.Join(",", validDates);
                    DicomLogger.Debug("WorklistSCP", "Date processing: Valid dates={ValidDates}, Selected date range: {StartDate} - {EndDate}",
                        validDatesStr, startDate, endDate);
                }
                else
                {
                    // If invalid dates are provided, use today's date range
                    startDate = today;
                    endDate = today;
                    DicomLogger.Debug("WorklistSCP", "Date processing: Invalid dates, using today: {Today}", today);
                }
            }
            else
            {
                // If empty date values are provided, use today's date range
                startDate = today;
                endDate = today;
                DicomLogger.Debug("WorklistSCP", "Date processing: Empty date values, using today: {Today}", today);
            }
        }
        else
        {
            // If no date parameters are provided, use a range from 30 days ago to 30 days in the future
            startDate = DateTime.Now.AddDays(-30).ToString("yyyyMMdd");
            endDate = DateTime.Now.AddDays(30).ToString("yyyyMMdd");
            DicomLogger.Debug("WorklistSCP", "Date processing: No date provided, using default range: {StartDate} - {EndDate}",
                startDate, endDate);
        }

        var dateRange = (StartDate: startDate, EndDate: endDate);
        DicomLogger.Information("WorklistSCP", "Final query date range: {StartDate} - {EndDate}",
            dateRange.StartDate, dateRange.EndDate);
        return dateRange;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Debug("WorklistSCP", "Received C-ECHO request");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    private string ConvertToDeviceName(string chineseName)
    {
        try
        {
            if (string.IsNullOrEmpty(chineseName))
                return "";

            var result = new StringBuilder();
            var isFirst = true;

            foreach (var c in chineseName)
            {
                if (PinyinHelper.IsChinese(c))
                {
                    var pinyin = PinyinHelper.GetPinyin(c);

                    // Capitalize the first letter
                    if (isFirst)
                    {
                        result.Append(char.ToUpper(pinyin[0]) + pinyin.Substring(1).ToLower());
                        isFirst = false;
                    }
                    else
                    {
                        result.Append(pinyin.ToLower());
                    }
                    result.Append('^'); // DICOM name separator
                }
                else
                {
                    // Directly add non-character symbols
                    result.Append(c);
                }
            }

            // Remove the last separator if it exists
            if (result.Length > 0 && result[result.Length - 1] == '^')
            {
                result.Length--;
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("WorklistSCP", ex, "Failed to convert patient name: {Name}", chineseName);
            return chineseName; // Return original name if conversion fails
        }
    }

    // Process DICOM value
    private string ProcessDicomValue(string value, DicomTag tag, bool needConvertName)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var vr = tag.DictionaryEntry.ValueRepresentations.First();
        string processedValue = value;

        // 1. Handle special requirements for CS type
        if (vr.Name == "CS")
        {
            // CS type character restrictions: only uppercase letters, digits, spaces, and underscores are allowed
            processedValue = new string(
                processedValue.ToUpperInvariant()
                    .Where(c => char.IsUpper(c) || char.IsDigit(c) || c == ' ' || c == '_')
                    .ToArray()
            ).Trim();
        }

        // 2. Convert Chinese characters if needed and the string contains Chinese characters
        if (needConvertName && ContainsChineseCharacters(processedValue))
        {
            processedValue = ConvertToDeviceName(processedValue);
        }

        return processedValue;
    }

    private bool ContainsChineseCharacters(string text)
    {
        return text.Any(c => PinyinHelper.IsChinese(c));
    }
}
