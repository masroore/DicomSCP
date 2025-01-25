using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using FellowOakDicom;
using DicomSCP.Services;
using DicomSCP.Configuration;
using DicomSCP.Models;
using FellowOakDicom.Network;

namespace DicomSCP.Controllers;

// 在文件开头添加传输语法枚举
public enum DicomTransferSyntaxType
{
    /// <summary>
    /// Implicit VR Little Endian - Default Transfer Syntax
    /// UID: 1.2.840.10008.1.2
    /// Code: IVLE
    /// </summary>
    ImplicitVRLittleEndian,

    /// <summary>
    /// Explicit VR Little Endian - Commonly used for network transfer
    /// UID: 1.2.840.10008.1.2.1
    /// Code: EVLE
    /// </summary>
    ExplicitVRLittleEndian,

    /// <summary>
    /// Explicit VR Big Endian - Deprecated, but may be needed for older devices
    /// UID: 1.2.840.10008.1.2.2
    /// Code: EVBE
    /// </summary>
    ExplicitVRBigEndian,

    /// <summary>
    /// JPEG Baseline (Process 1) - Lossy compression
    /// UID: 1.2.840.10008.1.2.4.50
    /// Code: JPEG_BASELINE
    /// </summary>
    JPEGBaseline,

    /// <summary>
    /// JPEG Lossless (Process 14) - Lossless compression
    /// UID: 1.2.840.10008.1.2.4.57
    /// Code: JPEG_LOSSLESS
    /// </summary>
    JPEGLossless,

    /// <summary>
    /// JPEG 2000 Lossy compression
    /// UID: 1.2.840.10008.1.2.4.91
    /// Code: JPEG2000_LOSSY
    /// </summary>
    JPEG2000Lossy,

    /// <summary>
    /// JPEG 2000 Lossless compression
    /// UID: 1.2.840.10008.1.2.4.90
    /// Code: JPEG2000_LOSSLESS
    /// </summary>
    JPEG2000Lossless,

    /// <summary>
    /// RLE Lossless compression
    /// UID: 1.2.840.10008.1.2.5
    /// Code: RLE
    /// </summary>
    RLELossless,

    /// <summary>
    /// JPEG-LS Lossless compression
    /// UID: 1.2.840.10008.1.2.4.80
    /// Code: JPEGLS_LOSSLESS
    /// </summary>
    JPEGLSLossless,

    /// <summary>
    /// JPEG-LS Near Lossless compression
    /// UID: 1.2.840.10008.1.2.4.81
    /// Code: JPEGLS_NEAR_LOSSLESS
    /// </summary>
    JPEGLSNearLossless
}

// 添加传输语法扩展方法
public static class DicomTransferSyntaxExtensions
{
    public static string GetUID(this DicomTransferSyntaxType transferSyntax)
    {
        return transferSyntax switch
        {
            DicomTransferSyntaxType.ImplicitVRLittleEndian => "1.2.840.10008.1.2",
            DicomTransferSyntaxType.ExplicitVRLittleEndian => "1.2.840.10008.1.2.1",
            DicomTransferSyntaxType.ExplicitVRBigEndian => "1.2.840.10008.1.2.2",
            DicomTransferSyntaxType.JPEGBaseline => "1.2.840.10008.1.2.4.50",
            DicomTransferSyntaxType.JPEGLossless => "1.2.840.10008.1.2.4.57",
            DicomTransferSyntaxType.JPEG2000Lossy => "1.2.840.10008.1.2.4.91",
            DicomTransferSyntaxType.JPEG2000Lossless => "1.2.840.10008.1.2.4.90",
            DicomTransferSyntaxType.RLELossless => "1.2.840.10008.1.2.5",
            DicomTransferSyntaxType.JPEGLSLossless => "1.2.840.10008.1.2.4.80",
            DicomTransferSyntaxType.JPEGLSNearLossless => "1.2.840.10008.1.2.4.81",
            _ => throw new ArgumentException($"Unsupported transfer syntax type: {transferSyntax}")
        };
    }

    public static string GetDescription(this DicomTransferSyntaxType transferSyntax)
    {
        return transferSyntax switch
        {
            DicomTransferSyntaxType.ImplicitVRLittleEndian => "Implicit VR Little Endian (default)",
            DicomTransferSyntaxType.ExplicitVRLittleEndian => "Explicit VR Little Endian",
            DicomTransferSyntaxType.ExplicitVRBigEndian => "Explicit VR Big Endian",
            DicomTransferSyntaxType.JPEGBaseline => "JPEG Baseline (lossy)",
            DicomTransferSyntaxType.JPEGLossless => "JPEG Lossless",
            DicomTransferSyntaxType.JPEG2000Lossy => "JPEG 2000 Lossy",
            DicomTransferSyntaxType.JPEG2000Lossless => "JPEG 2000 Lossless",
            DicomTransferSyntaxType.RLELossless => "RLE Lossless",
            DicomTransferSyntaxType.JPEGLSLossless => "JPEG-LS Lossless",
            DicomTransferSyntaxType.JPEGLSNearLossless => "JPEG-LS Near Lossless",
            _ => throw new ArgumentException($"Unsupported transfer syntax type: {transferSyntax}")
        };
    }
}

// 添加传输语法解析和验证类
public static class DicomTransferSyntaxParser
{
    private static readonly Dictionary<string, string> _uidMap = new()
    {
        { "1.2.840.10008.1.2", "ImplicitVRLittleEndian" },
        { "1.2.840.10008.1.2.1", "ExplicitVRLittleEndian" },
        { "1.2.840.10008.1.2.2", "ExplicitVRBigEndian" },
        { "1.2.840.10008.1.2.4.50", "JPEGBaseline" },
        { "1.2.840.10008.1.2.4.57", "JPEGLossless" },
        { "1.2.840.10008.1.2.4.91", "JPEG2000Lossy" },
        { "1.2.840.10008.1.2.4.90", "JPEG2000Lossless" },
        { "1.2.840.10008.1.2.5", "RLELossless" },
        { "1.2.840.10008.1.2.4.80", "JPEGLSLossless" },
        { "1.2.840.10008.1.2.4.81", "JPEGLSNearLossless" }
    };

    private static readonly Dictionary<string, string> _codeMap = new()
    {
        { "IVLE", "ImplicitVRLittleEndian" },
        { "EVLE", "ExplicitVRLittleEndian" },
        { "EVBE", "ExplicitVRBigEndian" },
        { "JPEG_BASELINE", "JPEGBaseline" },
        { "JPEG_LOSSLESS", "JPEGLossless" },
        { "JPEG2000_LOSSY", "JPEG2000Lossy" },
        { "JPEG2000_LOSSLESS", "JPEG2000Lossless" },
        { "RLE", "RLELossless" },
        { "JPEGLS_LOSSLESS", "JPEGLSLossless" },
        { "JPEGLS_NEAR_LOSSLESS", "JPEGLSNearLossless" }
    };

    public static DicomTransferSyntaxType? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // 1. Try to parse enum name directly
        if (Enum.TryParse<DicomTransferSyntaxType>(value, true, out var result))
            return result;

        // 2. Try to map from UID
        if (_uidMap.TryGetValue(value, out var uidMapped))
            if (Enum.TryParse<DicomTransferSyntaxType>(uidMapped, true, out result))
                return result;

        // 3. Try to map from code
        if (_codeMap.TryGetValue(value.ToUpper(), out var codeMapped))
            if (Enum.TryParse<DicomTransferSyntaxType>(codeMapped, true, out result))
                return result;

        throw new ArgumentException($"Unsupported transfer syntax: {value}");
    }
}

[ApiController]
[Route("api/[controller]")]
public class QueryRetrieveController : ControllerBase
{
    private readonly IQueryRetrieveSCU _queryRetrieveScu;
    private readonly QueryRetrieveConfig _config;
    private readonly DicomSettings _settings;
    private const string LogPrefix = "[Api]";

    public QueryRetrieveController(
        IQueryRetrieveSCU queryRetrieveScu,
        IOptions<QueryRetrieveConfig> config,
        IOptions<DicomSettings> settings)
    {
        _queryRetrieveScu = queryRetrieveScu;
        _config = config.Value;
        _settings = settings.Value;
    }

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        // Only return nodes that support query retrieve
        var qrNodes = _config.RemoteNodes.Where(n => n.SupportsQueryRetrieve());
        return Ok(qrNodes);
    }

    // Unified query interface
    [HttpPost("{nodeId}/query")]
    public async Task<ActionResult<IEnumerable<object>>> Query(
        string nodeId,
        [FromQuery] string level,
        [FromBody] QueryRequest queryParams)
    {
        var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
        if (node == null)
        {
            return NotFound($"Node not found: {nodeId}");
        }

        // Validate if the node supports query retrieve
        if (!node.SupportsQueryRetrieve())
        {
            return BadRequest($"Node {nodeId} does not support query retrieve operations");
        }

        // Parse query level
        if (!Enum.TryParse<DicomQueryRetrieveLevel>(level, true, out var queryLevel))
        {
            return BadRequest($"Invalid query level: {level}. Valid values are: Patient, Study, Series, Image");
        }

        // Validate Patient level query parameters
        if (queryLevel == DicomQueryRetrieveLevel.Patient && !string.IsNullOrEmpty(queryParams.StudyInstanceUid))
        {
            DicomLogger.Warning(LogPrefix, "Patient level query should not include StudyInstanceUID");
        }

        // Special validation for Image level query
        if (queryLevel == DicomQueryRetrieveLevel.Image && !queryParams.ValidateImageLevelQuery())
        {
            return BadRequest(new QueryResponse<object>
            {
                Success = false,
                Message = "Image level query must provide StudyInstanceUID and SeriesInstanceUID"
            });
        }

        try
        {
            var queryDict = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(queryParams.PatientId))
                queryDict["patientId"] = queryParams.PatientId;
            if (!string.IsNullOrEmpty(queryParams.PatientName))
                queryDict["patientName"] = queryParams.PatientName;
            if (!string.IsNullOrEmpty(queryParams.AccessionNumber))
                queryDict["accessionNumber"] = queryParams.AccessionNumber;
            if (!string.IsNullOrEmpty(queryParams.StudyDate))
                queryDict["studyDate"] = queryParams.StudyDate;
            if (!string.IsNullOrEmpty(queryParams.Modality))
                queryDict["modality"] = queryParams.Modality;
            if (!string.IsNullOrEmpty(queryParams.StudyInstanceUid))
                queryDict["studyInstanceUid"] = queryParams.StudyInstanceUid;
            if (!string.IsNullOrEmpty(queryParams.SeriesInstanceUid))
                queryDict["seriesInstanceUid"] = queryParams.SeriesInstanceUid;

            // Special parameters for Image level - do not add sopInstanceUid as a query condition
            if (!string.IsNullOrEmpty(queryParams.InstanceNumber))
            {
                queryDict["instanceNumber"] = queryParams.InstanceNumber;
                DicomLogger.Debug(LogPrefix, "Added InstanceNumber parameter: {0}", queryParams.InstanceNumber);
            }

            DicomLogger.Debug(LogPrefix, "Query parameter dictionary: {0}",
                string.Join(", ", queryDict.Select(kv => $"{kv.Key}={kv.Value}")));

            var dataset = BuildQueryDataset(queryLevel, queryDict);
            var results = await _queryRetrieveScu.QueryAsync(node, queryLevel, dataset);

            // Log raw results
            DicomLogger.Debug(LogPrefix, "Received query results - Level: {Level}, Count: {Count}",
                queryLevel, results?.Count() ?? 0);

            // Ensure results are not empty
            if (results == null || !results.Any())
            {
                DicomLogger.Warning(LogPrefix, "Query returned no data - Level: {Level}, Node: {Node}",
                    queryLevel, nodeId);
                return Ok(new QueryResponse<object>
                {
                    Success = true,
                    Data = Array.Empty<object>(),
                    Total = 0,
                    Message = "No matching data found"
                });
            }

            // Special handling for Image level
            if (queryLevel == DicomQueryRetrieveLevel.Image)
            {
                var imageResults = results.Select(DicomImageResult.FromDataset).ToList();
                DicomLogger.Information(LogPrefix, "Image level query returned {0} results", imageResults.Count);
                return Ok(new QueryResponse<DicomImageResult>
                {
                    Success = true,
                    Data = imageResults,
                    Total = imageResults.Count
                });
            }

            // Return different response types based on query level
            switch (queryLevel)
            {
                case DicomQueryRetrieveLevel.Patient:
                    return Ok(new QueryResponse<DicomPatientResult>
                    {
                        Success = true,
                        Data = results.Select(DicomPatientResult.FromDataset),
                        Total = results.Count()
                    });
                case DicomQueryRetrieveLevel.Study:
                    return Ok(new QueryResponse<DicomStudyResult>
                    {
                        Success = true,
                        Data = results.Select(DicomStudyResult.FromDataset),
                        Total = results.Count()
                    });
                case DicomQueryRetrieveLevel.Series:
                    return Ok(new QueryResponse<DicomSeriesResult>
                    {
                        Success = true,
                        Data = results.Select(DicomSeriesResult.FromDataset),
                        Total = results.Count()
                    });
                case DicomQueryRetrieveLevel.Image:
                    var imageResults = results.Select(DicomImageResult.FromDataset).ToList();
                    DicomLogger.Information(LogPrefix, "Image level query returned {0} results", imageResults.Count);
                    return Ok(new QueryResponse<DicomImageResult>
                    {
                        Success = true,
                        Data = imageResults,
                        Total = imageResults.Count
                    });
                default:
                    return BadRequest(new QueryResponse<object>
                    {
                        Success = false,
                        Message = $"Unsupported query level: {level}"
                    });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error(LogPrefix, ex, "Failed to execute {Level} query", level);
            return StatusCode(500, new QueryResponse<object>
            {
                Success = false,
                Message = ex.Message
            });
        }
    }

    /// <summary>
    /// Send DICOM move request
    /// </summary>
    [HttpPost("{nodeId}/move")]
    [ProducesResponseType(typeof(MoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MoveResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MoveResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MoveResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> Move(
        string nodeId,
        [FromQuery] string level,
        [FromBody] MoveRequest moveRequest)
    {
        try
        {
            var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
            if (node == null)
            {
                return NotFound($"Node not found: {nodeId}");
            }

            // Validate if the node supports query retrieve
            if (!node.SupportsQueryRetrieve())
            {
                return BadRequest($"Node {nodeId} does not support query retrieve operations");
            }

            // Parse level
            if (!Enum.TryParse<DicomQueryRetrieveLevel>(level, true, out var queryLevel))
            {
                return BadRequest(new MoveResponse
                {
                    Success = false,
                    Message = $"Invalid move level: {level}. Valid values are: Patient, Study, Series, Image"
                });
            }

            // Validate request parameters
            var (isValid, errorMessage) = ValidateMoveRequest(queryLevel, moveRequest);
            if (!isValid)
            {
                return BadRequest(new MoveResponse
                {
                    Success = false,
                    Message = errorMessage
                });
            }

            try
            {
                // Build dataset
                var dataset = new DicomDataset();
                dataset.Add(DicomTag.QueryRetrieveLevel, queryLevel.ToString().ToUpper());

                // Add necessary fields based on level
                switch (queryLevel)
                {
                    case DicomQueryRetrieveLevel.Patient:
                        dataset.Add(DicomTag.PatientID, moveRequest.PatientId);
                        break;

                    case DicomQueryRetrieveLevel.Study:
                        dataset.Add(DicomTag.StudyInstanceUID, moveRequest.StudyInstanceUid);
                        break;

                    case DicomQueryRetrieveLevel.Series:
                        dataset.Add(DicomTag.StudyInstanceUID, moveRequest.StudyInstanceUid);
                        dataset.Add(DicomTag.SeriesInstanceUID, moveRequest.SeriesInstanceUid);
                        break;

                    case DicomQueryRetrieveLevel.Image:
                        dataset.Add(DicomTag.StudyInstanceUID, moveRequest.StudyInstanceUid);
                        dataset.Add(DicomTag.SeriesInstanceUID, moveRequest.SeriesInstanceUid);
                        dataset.Add(DicomTag.SOPInstanceUID, moveRequest.SopInstanceUid);
                        break;
                }

                DicomLogger.Debug(LogPrefix, "Move request dataset: {0}", dataset.ToString());

                // Parse transfer syntax
                string? transferSyntax = null;
                if (!string.IsNullOrEmpty(moveRequest.TransferSyntax))
                {
                    try
                    {
                        var syntaxType = DicomTransferSyntaxParser.Parse(moveRequest.TransferSyntax);
                        if (syntaxType.HasValue)
                        {
                            transferSyntax = syntaxType.Value.GetUID();
                            DicomLogger.Debug(LogPrefix,
                                "Using specified transfer syntax: {0} ({1}) [Input: {2}]",
                                syntaxType.Value.GetDescription(),
                                transferSyntax,
                                moveRequest.TransferSyntax);
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        return BadRequest(new MoveResponse
                        {
                            Success = false,
                            Message = ex.Message
                        });
                    }
                }

                // Use local AE Title and pass transfer syntax parameter
                var success = await _queryRetrieveScu.MoveAsync(
                    node,
                    queryLevel,
                    dataset,
                    _settings.AeTitle,
                    transferSyntax);

                if (!success)
                {
                    // Return different error messages based on the situation
                    return StatusCode(500, new MoveResponse
                    {
                        Success = false,
                        Message = queryLevel == DicomQueryRetrieveLevel.Patient ?
                            "Patient level move did not return any images, this level may not be supported, please try using Study level" :
                            "Move request was rejected"
                    });
                }

                return Ok(new MoveResponse
                {
                    Success = true,
                    Message = queryLevel == DicomQueryRetrieveLevel.Patient ?
                        "Patient level move request sent, if supported, you can view the images later in the image management" :
                        "Move request sent, please check the image management later",
                    JobId = Guid.NewGuid().ToString()
                });
            }
            catch (Exception ex)
            {
                DicomLogger.Error(LogPrefix, ex, "Failed to send {Level} move request", level);
                return StatusCode(500, new MoveResponse
                {
                    Success = false,
                    Message = "Failed to send move request"
                });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error(LogPrefix, ex, "Failed to execute {Level} move request", level);
            return StatusCode(500, new MoveResponse
            {
                Success = false,
                Message = "Failed to execute move request"
            });
        }
    }

    // Add validation method
    private (bool IsValid, string ErrorMessage) ValidateMoveRequest(DicomQueryRetrieveLevel level, MoveRequest request)
    {
        switch (level)
        {
            case DicomQueryRetrieveLevel.Patient:
                if (string.IsNullOrEmpty(request.PatientId))
                {
                    return (false, "Patient level move must provide PatientId");
                }
                break;

            case DicomQueryRetrieveLevel.Study:
                if (string.IsNullOrEmpty(request.StudyInstanceUid))
                {
                    return (false, "Study level move must provide StudyInstanceUID");
                }
                break;

            case DicomQueryRetrieveLevel.Series:
                if (string.IsNullOrEmpty(request.StudyInstanceUid) ||
                    string.IsNullOrEmpty(request.SeriesInstanceUid))
                {
                    return (false, "Series level move must provide StudyInstanceUID and SeriesInstanceUID");
                }
                break;

            case DicomQueryRetrieveLevel.Image:
                if (string.IsNullOrEmpty(request.StudyInstanceUid) ||
                    string.IsNullOrEmpty(request.SeriesInstanceUid) ||
                    string.IsNullOrEmpty(request.SopInstanceUid))
                {
                    return (false, "Image level move must provide StudyInstanceUID, SeriesInstanceUID, and SopInstanceUID");
                }
                break;
        }

        return (true, string.Empty);
    }

    // Helper method: build query dataset
    private DicomDataset BuildQueryDataset(DicomQueryRetrieveLevel level, Dictionary<string, string> queryParams)
    {
        var dataset = new DicomDataset();
        dataset.Add(DicomTag.QueryRetrieveLevel, level.ToString().ToUpper());

        switch (level)
        {
            case DicomQueryRetrieveLevel.Patient:
                AddPatientQueryFields(dataset, queryParams);
                break;
            case DicomQueryRetrieveLevel.Study:
                AddStudyQueryFields(dataset, queryParams);
                break;
            case DicomQueryRetrieveLevel.Series:
                AddSeriesQueryFields(dataset, queryParams);
                break;
            case DicomQueryRetrieveLevel.Image:
                AddImageQueryFields(dataset, queryParams);
                break;
        }

        return dataset;
    }

    // Helper method: format query results
    private object FormatQueryResults(DicomQueryRetrieveLevel level, IEnumerable<DicomDataset> results)
    {
        switch (level)
        {
            case DicomQueryRetrieveLevel.Study:
                return results.Select(DicomStudyResult.FromDataset);
            case DicomQueryRetrieveLevel.Series:
                return results.Select(DicomSeriesResult.FromDataset);
            case DicomQueryRetrieveLevel.Image:
                return results.Select(DicomImageResult.FromDataset);
            default:
                return results;
        }
    }

    // Helper method: build move dataset
    private DicomDataset BuildMoveDataset(DicomQueryRetrieveLevel level, Dictionary<string, string> moveRequest)
    {
        var dataset = new DicomDataset();
        dataset.Add(DicomTag.QueryRetrieveLevel, level.ToString().ToUpper());

        switch (level)
        {
            case DicomQueryRetrieveLevel.Study:
                if (moveRequest.TryGetValue("studyInstanceUid", out string? studyUid1) && !string.IsNullOrEmpty(studyUid1))
                {
                    dataset.Add(DicomTag.StudyInstanceUID, studyUid1);
                }
                break;

            case DicomQueryRetrieveLevel.Series:
                if (moveRequest.TryGetValue("studyInstanceUid", out string? studyUid2) && !string.IsNullOrEmpty(studyUid2))
                {
                    dataset.Add(DicomTag.StudyInstanceUID, studyUid2);
                }
                if (moveRequest.TryGetValue("seriesInstanceUid", out string? seriesUid1) && !string.IsNullOrEmpty(seriesUid1))
                {
                    dataset.Add(DicomTag.SeriesInstanceUID, seriesUid1);
                }
                break;

            case DicomQueryRetrieveLevel.Image:
                if (moveRequest.TryGetValue("studyInstanceUid", out string? studyUid3) && !string.IsNullOrEmpty(studyUid3))
                {
                    dataset.Add(DicomTag.StudyInstanceUID, studyUid3);
                }
                if (moveRequest.TryGetValue("seriesInstanceUid", out string? seriesUid2) && !string.IsNullOrEmpty(seriesUid2))
                {
                    dataset.Add(DicomTag.SeriesInstanceUID, seriesUid2);
                }
                if (moveRequest.TryGetValue("sopInstanceUid", out string? sopUid) && !string.IsNullOrEmpty(sopUid))
                {
                    dataset.Add(DicomTag.SOPInstanceUID, sopUid);
                }
                break;
        }

        return dataset;
    }

    // Add patient query fields
    private void AddPatientQueryFields(DicomDataset dataset, Dictionary<string, string> queryParams)
    {
        // Required return fields
        dataset.Add(DicomTag.PatientID, "");
        dataset.Add(DicomTag.PatientName, "");
        dataset.Add(DicomTag.PatientBirthDate, "");
        dataset.Add(DicomTag.PatientSex, "");
        dataset.Add(DicomTag.NumberOfPatientRelatedStudies, "");

        // Only handle Patient level query conditions
        if (queryParams.TryGetValue("patientId", out var patientId) && !string.IsNullOrWhiteSpace(patientId))
        {
            dataset.AddOrUpdate(DicomTag.PatientID, patientId);
            DicomLogger.Debug(LogPrefix, "Patient query - Added PatientID: {0}", patientId);
        }

        if (queryParams.TryGetValue("patientName", out var patientName) && !string.IsNullOrWhiteSpace(patientName))
        {
            dataset.AddOrUpdate(DicomTag.PatientName, $"*{patientName}*");
            DicomLogger.Debug(LogPrefix, "Patient query - Added PatientName: {0}", patientName);
        }

        if (queryParams.TryGetValue("patientBirthDate", out var birthDate) && !string.IsNullOrWhiteSpace(birthDate))
        {
            dataset.AddOrUpdate(DicomTag.PatientBirthDate, birthDate);
            DicomLogger.Debug(LogPrefix, "Patient query - Added PatientBirthDate: {0}", birthDate);
        }

        if (queryParams.TryGetValue("patientSex", out var sex) && !string.IsNullOrWhiteSpace(sex))
        {
            dataset.AddOrUpdate(DicomTag.PatientSex, sex);
            DicomLogger.Debug(LogPrefix, "Patient query - Added PatientSex: {0}", sex);
        }

        // Ignore other level query conditions (e.g., studyInstanceUid)
        if (queryParams.ContainsKey("studyInstanceUid"))
        {
            DicomLogger.Warning(LogPrefix, "Patient query should not use StudyInstanceUID as a query condition");
        }

        DicomLogger.Debug(LogPrefix, "Patient level query dataset: {0}", dataset.ToString());
    }

    // Add study level query fields
    private void AddStudyQueryFields(DicomDataset dataset, Dictionary<string, string> queryParams)
    {
        // Required return fields
        dataset.Add(DicomTag.StudyInstanceUID, "");
        dataset.Add(DicomTag.StudyDate, "");
        dataset.Add(DicomTag.StudyTime, "");
        dataset.Add(DicomTag.PatientName, "");
        dataset.Add(DicomTag.PatientID, "");
        dataset.Add(DicomTag.StudyDescription, "");
        dataset.Add(DicomTag.ModalitiesInStudy, "");
        dataset.Add(DicomTag.NumberOfStudyRelatedSeries, "");
        dataset.Add(DicomTag.NumberOfStudyRelatedInstances, "");
        dataset.Add(DicomTag.AccessionNumber, "");

        // Handle query conditions
        if (queryParams.TryGetValue("patientId", out var patientId) && !string.IsNullOrWhiteSpace(patientId))
        {
            dataset.AddOrUpdate(DicomTag.PatientID, $"*{patientId}*");
        }

        if (queryParams.TryGetValue("patientName", out var patientName) && !string.IsNullOrWhiteSpace(patientName))
        {
            dataset.AddOrUpdate(DicomTag.PatientName, $"*{patientName}*");
        }

        if (queryParams.TryGetValue("accessionNumber", out var accessionNumber) && !string.IsNullOrWhiteSpace(accessionNumber))
        {
            dataset.AddOrUpdate(DicomTag.AccessionNumber, $"*{accessionNumber}*");
        }

        if (queryParams.TryGetValue("modality", out var modality) && !string.IsNullOrWhiteSpace(modality))
        {
            dataset.AddOrUpdate(DicomTag.ModalitiesInStudy, modality);
        }

        if (queryParams.TryGetValue("studyDate", out var studyDate) && !string.IsNullOrWhiteSpace(studyDate))
        {
            // Convert to DICOM date format YYYYMMDD
            var dicomDate = studyDate.Replace("-", "");
            dataset.AddOrUpdate(DicomTag.StudyDate, dicomDate);
        }
    }

    // Add series level query fields
    private void AddSeriesQueryFields(DicomDataset dataset, Dictionary<string, string> queryParams)
    {
        // Required return fields
        dataset.Add(DicomTag.SeriesInstanceUID, "");
        dataset.Add(DicomTag.StudyInstanceUID, "");  // Add StudyInstanceUID
        dataset.Add(DicomTag.SeriesNumber, "");
        dataset.Add(DicomTag.SeriesDescription, "");
        dataset.Add(DicomTag.Modality, "");
        dataset.Add(DicomTag.NumberOfSeriesRelatedInstances, "");

        // Required parent fields
        if (queryParams.TryGetValue("studyInstanceUid", out var studyUid))
        {
            dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        }

        // Handle query conditions
        if (queryParams.TryGetValue("seriesInstanceUid", out var seriesUid))
        {
            dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        }

        if (queryParams.TryGetValue("seriesNumber", out var seriesNumber))
        {
            dataset.AddOrUpdate(DicomTag.SeriesNumber, seriesNumber);
        }

        if (queryParams.TryGetValue("seriesDescription", out var seriesDescription))
        {
            dataset.AddOrUpdate(DicomTag.SeriesDescription, seriesDescription);
        }

        if (queryParams.TryGetValue("seriesModality", out var modality))
        {
            dataset.AddOrUpdate(DicomTag.Modality, modality);
        }
    }

    // Add image level query fields
    private void AddImageQueryFields(DicomDataset dataset, Dictionary<string, string> queryParams)
    {
        // Required return fields
        dataset.Add(DicomTag.SOPInstanceUID, "");
        dataset.Add(DicomTag.StudyInstanceUID, "");
        dataset.Add(DicomTag.SeriesInstanceUID, "");
        dataset.Add(DicomTag.InstanceNumber, "");
        dataset.Add(DicomTag.ImageType, "");
        dataset.Add(DicomTag.Rows, "");
        dataset.Add(DicomTag.Columns, "");
        dataset.Add(DicomTag.BitsAllocated, "");
        dataset.Add(DicomTag.NumberOfFrames, "");
        dataset.Add(DicomTag.SOPClassUID, "");

        // Required parent fields - these are used to limit the query scope
        if (queryParams.TryGetValue("studyInstanceUid", out var studyUid))
        {
            dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            DicomLogger.Debug(LogPrefix, "Added StudyInstanceUID: {0}", studyUid);
        }
        if (queryParams.TryGetValue("seriesInstanceUid", out var seriesUid))
        {
            dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
            DicomLogger.Debug(LogPrefix, "Added SeriesInstanceUID: {0}", seriesUid);
        }

        // Note: Typically, SOPInstanceUID should not be used in queries,
        // as we want to retrieve all images under the series.
        // If a specific image is needed, it should be filtered in the results.

        if (queryParams.TryGetValue("instanceNumber", out var instanceNumber))
        {
            dataset.AddOrUpdate(DicomTag.InstanceNumber, instanceNumber);
            DicomLogger.Debug(LogPrefix, "Added InstanceNumber: {0}", instanceNumber);
        }

        // Log the complete query dataset
        DicomLogger.Debug(LogPrefix, "Image level query dataset: {0}", dataset.ToString());
    }

    [HttpPost("{nodeId}/verify")]
    public async Task<IActionResult> VerifyConnection(string nodeId)
    {
        try
        {
            var node = _config.RemoteNodes.FirstOrDefault(n => n.Name == nodeId);
            if (node == null)
            {
                return NotFound($"Node not found: {nodeId}");
            }

            // Validate if the node supports query retrieve
            if (!node.SupportsQueryRetrieve())
            {
                return BadRequest($"Node {nodeId} does not support query retrieve operations");
            }

            var success = await _queryRetrieveScu.VerifyConnectionAsync(node);

            if (!success)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Connection test failed",
                    details = new
                    {
                        localAe = _config.LocalAeTitle,
                        remoteAe = node.AeTitle,
                        host = node.HostName,
                        port = node.Port
                    }
                });
            }

            return Ok(new
            {
                success = true,
                message = "Connection test succeeded",
                details = new
                {
                    localAe = _config.LocalAeTitle,
                    remoteAe = node.AeTitle,
                    host = node.HostName,
                    port = node.Port
                }
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error(LogPrefix, ex, "Failed to execute connection test");
            return StatusCode(500, new { success = false, message = "Failed to execute connection test", error = ex.Message });
        }
    }

    // ... other helper methods
}

// Add classes for converting query results
public class DicomStudyResult
{
    private const string LogPrefix = "[Api]";

    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public DateTime? StudyDate { get; set; }
    public string StudyDescription { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public int NumberOfSeries { get; set; }
    public int NumberOfInstances { get; set; }

    public static DicomStudyResult FromDataset(DicomDataset dataset)
    {
        DateTime? studyDate = null;
        try
        {
            var dateStr = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty);
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length == 8)
            {
                var year = int.Parse(dateStr.Substring(0, 4));
                var month = int.Parse(dateStr.Substring(4, 2));
                var day = int.Parse(dateStr.Substring(6, 2));
                studyDate = new DateTime(year, month, day);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Warning(LogPrefix, ex, "解析研究日期失败");
        }

        return new DicomStudyResult
        {
            PatientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            AccessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            Modality = dataset.GetSingleValueOrDefault(DicomTag.ModalitiesInStudy, string.Empty),
            StudyDate = studyDate,
            StudyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
            StudyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            NumberOfSeries = dataset.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedSeries, 0),
            NumberOfInstances = dataset.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedInstances, 0)
        };
    }
}

// Add Series result conversion class
public class DicomSeriesResult
{
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string SeriesNumber { get; set; } = string.Empty;
    public string SeriesDescription { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public int NumberOfInstances { get; set; }

    public static DicomSeriesResult FromDataset(DicomDataset dataset)
    {
        return new DicomSeriesResult
        {
            SeriesInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
            StudyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            SeriesNumber = dataset.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty),
            SeriesDescription = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
            Modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
            NumberOfInstances = dataset.GetSingleValueOrDefault(DicomTag.NumberOfSeriesRelatedInstances, 0)
        };
    }
}

// 添加Image级别的结果转换类
public class DicomImageResult
{
    public string SopInstanceUid { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string InstanceNumber { get; set; } = string.Empty;
    public string[] ImageType { get; set; } = Array.Empty<string>();
    public int Rows { get; set; }
    public int Columns { get; set; }
    public int BitsAllocated { get; set; }
    public int NumberOfFrames { get; set; }

    public static DicomImageResult FromDataset(DicomDataset dataset)
    {
        return new DicomImageResult
        {
            SopInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty),
            StudyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            SeriesInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
            InstanceNumber = dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, string.Empty),
            ImageType = dataset.GetValues<string>(DicomTag.ImageType).ToArray(),
            Rows = dataset.GetSingleValueOrDefault(DicomTag.Rows, 0),
            Columns = dataset.GetSingleValueOrDefault(DicomTag.Columns, 0),
            BitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, 0),
            NumberOfFrames = dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1)
        };
    }
}

// Add Patient level result conversion class
public class DicomPatientResult
{
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string PatientSex { get; set; } = string.Empty;
    public int NumberOfStudies { get; set; }

    public static DicomPatientResult FromDataset(DicomDataset dataset)
    {
        return new DicomPatientResult
        {
            PatientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            PatientBirthDate = dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
            PatientSex = dataset.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty),
            NumberOfStudies = dataset.GetSingleValueOrDefault(DicomTag.NumberOfPatientRelatedStudies, 0)
        };
    }
}

// Add transfer syntax parameter in MoveRequest class
public class MoveRequest
{
    public string PatientId { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string SopInstanceUid { get; set; } = string.Empty;
    // 使用字符串，可以接受 UID、代码或枚举名称
    public string? TransferSyntax { get; set; }
}
