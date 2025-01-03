using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DicomSCP.Data;
using DicomSCP.Configuration;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.Text.Json;
using DicomSCP.Services;

namespace DicomSCP.Controllers
{
    [ApiController]
    [Route("dicomweb")]
    [AllowAnonymous]
    public class DicomWebController : ControllerBase
    {
        private readonly ILogger<DicomWebController> _logger;
        private readonly DicomRepository _repository;
        private readonly DicomSettings _settings;
        private const string AppDicomContentType = "application/dicom";
        private const string JpegImageContentType = "image/jpeg";
        private const string DicomJsonContentType = "application/dicom+json";
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = true
        };

        public DicomWebController(
            DicomRepository repository,
            IOptions<DicomSettings> settings,
            ILogger<DicomWebController> logger)
        {
            _repository = repository;
            _settings = settings.Value;
            _logger = logger;
        }

        #region QIDO-RS 查询接口

        // QIDO-RS: 查询研究
        [HttpGet("studies")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> SearchStudies(
            [FromQuery(Name = "PatientID")] string? patientId = null,
            [FromQuery(Name = "PatientName")] string? patientName = null,
            [FromQuery(Name = "StudyDate")] string? studyDate = null,
            [FromQuery(Name = "StudyInstanceUID")] string? studyInstanceUid = null,
            [FromQuery(Name = "AccessionNumber")] string? accessionNumber = null,
            [FromQuery(Name = "ModalitiesInStudy")] string? modalitiesInStudy = null,
            [FromQuery(Name = "offset")] int offset = 0,
            [FromQuery(Name = "limit")] int limit = 100)
        {
            try
            {
                var studies = await _repository.GetStudiesAsync(
                    page: (offset / limit) + 1,
                    pageSize: limit,
                    patientId: patientId,
                    patientName: patientName,
                    accessionNumber: accessionNumber,
                    modality: modalitiesInStudy,
                    startDate: GetStartDate(studyDate),
                    endDate: GetEndDate(studyDate));

                if (!string.IsNullOrEmpty(studyInstanceUid))
                {
                    studies.Items = studies.Items.Where(s => s.StudyInstanceUid == studyInstanceUid).ToList();
                }

                Response.Headers.Append("X-Total-Count", studies.TotalCount.ToString());

                var result = studies.Items.Select(s => new Dictionary<string, object>
                {
                    { "00080020", new { vr = "DA", Value = new[] { s.StudyDate } } },
                    { "00080050", new { vr = "SH", Value = new[] { s.AccessionNumber } } },
                    { "00080061", new { vr = "CS", Value = s.Modality?.Split('\\') } },
                    { "00081030", new { vr = "LO", Value = new[] { s.StudyDescription } } },
                    { "00100010", new { vr = "PN", Value = new[] { s.PatientName } } },
                    { "00100020", new { vr = "LO", Value = new[] { s.PatientId } } },
                    { "00100030", new { vr = "DA", Value = new[] { s.PatientBirthDate } } },
                    { "00100040", new { vr = "CS", Value = new[] { s.PatientSex } } },
                    { "0020000D", new { vr = "UI", Value = new[] { s.StudyInstanceUid } } },
                    { "00201208", new { vr = "IS", Value = new[] { s.NumberOfInstances.ToString() } } }
                });

                return new JsonResult(result, _jsonOptions);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - QIDO-RS 查询研究失败");
                return StatusCode(500, "Error searching studies");
            }
        }

        // QIDO-RS: 查询序列
        [HttpGet("studies/{StudyInstanceUID}/series")]
        [Produces("application/dicom+json")]
        public IActionResult SearchSeries(
            string StudyInstanceUID,
            [FromQuery(Name = "SeriesInstanceUID")] string? SeriesInstanceUID = null,
            [FromQuery(Name = "Modality")] string? Modality = null)
        {
            try
            {
                var series = _repository.GetSeriesByStudyUid(StudyInstanceUID).ToList();
                
                if (!string.IsNullOrEmpty(SeriesInstanceUID))
                {
                    series = series.Where(s => s.SeriesInstanceUid == SeriesInstanceUID).ToList();
                }
                if (!string.IsNullOrEmpty(Modality))
                {
                    series = series.Where(s => s.Modality == Modality).ToList();
                }

                if (!series.Any())
                {
                    return NotFound();
                }

                var result = series.Select(s => new Dictionary<string, object>
                {
                    { "00080060", new { vr = "CS", Value = new[] { s.Modality } } },
                    { "0020000E", new { vr = "UI", Value = new[] { s.SeriesInstanceUid } } },
                    { "00200011", new { vr = "IS", Value = new[] { s.SeriesNumber } } },
                    { "0008103E", new { vr = "LO", Value = new[] { s.SeriesDescription } } },
                    { "00201209", new { vr = "IS", Value = new[] { s.NumberOfInstances.ToString() } } },
                    { "00081190", new { vr = "UR", Value = new[] { 
                        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/dicomweb/studies/{StudyInstanceUID}/series/{s.SeriesInstanceUid}" 
                    }}}
                }).ToList();

                return new JsonResult(result, _jsonOptions);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - QIDO-RS 查询序列失败");
                return StatusCode(500, "Error searching series");
            }
        }

        // QIDO-RS: 查询实例
        [HttpGet("studies/{StudyInstanceUID}/series/{SeriesInstanceUID}/instances")]
        [Produces("application/dicom+json")]
        public IActionResult SearchInstances(
            string StudyInstanceUID,
            string SeriesInstanceUID,
            [FromQuery(Name = "SOPInstanceUID")] string? SOPInstanceUID = null)
        {
            try
            {
                var instances = _repository.GetInstancesBySeriesUid(StudyInstanceUID, SeriesInstanceUID).ToList();
                
                if (!string.IsNullOrEmpty(SOPInstanceUID))
                {
                    instances = instances.Where(i => i.SopInstanceUid == SOPInstanceUID).ToList();
                }

                if (!instances.Any())
                {
                    return NotFound();
                }

                var result = instances.Select(i => new Dictionary<string, object>
                {
                    { "00080016", new { vr = "UI", Value = new[] { i.SopClassUid } } },
                    { "00080018", new { vr = "UI", Value = new[] { i.SopInstanceUid } } },
                    { "00200013", new { vr = "IS", Value = new[] { i.InstanceNumber } } },
                    { "00081190", new { vr = "UR", Value = new[] { 
                        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/dicomweb/studies/{StudyInstanceUID}/series/{SeriesInstanceUID}/instances/{i.SopInstanceUid}" 
                    }}}
                }).ToList();

                return new JsonResult(result, _jsonOptions);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - QIDO-RS 查询实例失败");
                return StatusCode(500, "Error searching instances");
            }
        }

        #endregion

        #region WADO-RS 元数据接口

        // WADO-RS: 检索研究元数据
        [HttpGet("studies/{studyInstanceUid}/metadata")]
        [Produces(DicomJsonContentType)]
        public async Task<IActionResult> RetrieveStudyMetadata(string studyInstanceUid)
        {
            try
            {
                var study = await _repository.GetStudyAsync(studyInstanceUid);
                if (study == null)
                {
                    return NotFound("Study not found");
                }

                var metadata = new Dictionary<string, object>
                {
                    // 患者信息
                    { "00100010", new { vr = "PN", Value = new[] { study.PatientName } } },
                    { "00100020", new { vr = "LO", Value = new[] { study.PatientId } } },
                    { "00100030", new { vr = "DA", Value = new[] { study.PatientBirthDate } } },
                    { "00100040", new { vr = "CS", Value = new[] { study.PatientSex } } },
                    
                    // 研究信息
                    { "0020000D", new { vr = "UI", Value = new[] { study.StudyInstanceUid } } },
                    { "00080020", new { vr = "DA", Value = new[] { study.StudyDate } } },
                    { "00080030", new { vr = "TM", Value = new[] { study.StudyTime } } },
                    { "00081030", new { vr = "LO", Value = new[] { study.StudyDescription } } },
                    { "00080050", new { vr = "SH", Value = new[] { study.AccessionNumber } } },
                    { "00080061", new { vr = "CS", Value = study.Modality?.Split('\\') } },
                    { "00080080", new { vr = "LO", Value = new[] { study.InstitutionName } } },
                    { "00201206", new { vr = "IS", Value = new[] { study.NumberOfStudyRelatedSeries.ToString() } } },
                    { "00201208", new { vr = "IS", Value = new[] { study.NumberOfStudyRelatedInstances.ToString() } } }
                };

                return Ok(new[] { metadata });
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检索研究元数据失败");
                return StatusCode(500, "Error retrieving study metadata");
            }
        }

        // WADO-RS: 检索序列元数据
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/metadata")]
        [Produces(DicomJsonContentType)]
        public async Task<IActionResult> RetrieveSeriesMetadata(
            string studyInstanceUid,
            string seriesInstanceUid)
        {
            try
            {
                var series = await _repository.GetSeriesAsync(studyInstanceUid);
                var seriesInfo = series.FirstOrDefault(s => s.SeriesInstanceUid == seriesInstanceUid);
                if (seriesInfo == null)
                {
                    return NotFound("Series not found");
                }

                var metadata = new Dictionary<string, object>
                {
                    // 序列信息
                    { "0020000E", new { vr = "UI", Value = new[] { seriesInfo.SeriesInstanceUid } } },
                    { "00080060", new { vr = "CS", Value = new[] { seriesInfo.Modality } } },
                    { "00200011", new { vr = "IS", Value = new[] { seriesInfo.SeriesNumber } } },
                    { "0008103E", new { vr = "LO", Value = new[] { seriesInfo.SeriesDescription } } },
                    { "00080021", new { vr = "DA", Value = new[] { seriesInfo.SeriesDate } } },
                    { "00201209", new { vr = "IS", Value = new[] { seriesInfo.NumberOfInstances.ToString() } } },
                    { "00180015", new { vr = "CS", Value = new[] { seriesInfo.SliceThickness } } }
                };

                return Ok(new[] { metadata });
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检索序列元数据失败");
                return StatusCode(500, "Error retrieving series metadata");
            }
        }

        // WADO-RS: 检索实例元数据
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/metadata")]
        [Produces(DicomJsonContentType)]
        public async Task<IActionResult> RetrieveInstanceMetadata(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid)
        {
            try
            {
                var instances = await Task.FromResult(_repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
                var instance = instances.FirstOrDefault(i => i.SopInstanceUid == sopInstanceUid);
                if (instance == null)
                {
                    return NotFound("Instance not found");
                }

                var metadata = new Dictionary<string, object>
                {
                    // 实例信息
                    { "00080016", new { vr = "UI", Value = new[] { instance.SopClassUid } } },
                    { "00080018", new { vr = "UI", Value = new[] { instance.SopInstanceUid } } },
                    { "00200013", new { vr = "IS", Value = new[] { instance.InstanceNumber } } },
                    
                    // 图像信息
                    { "00280010", new { vr = "US", Value = new[] { instance.Rows.ToString() } } },
                    { "00280011", new { vr = "US", Value = new[] { instance.Columns.ToString() } } },
                    { "00280100", new { vr = "US", Value = new[] { instance.BitsAllocated.ToString() } } },
                    { "00280101", new { vr = "US", Value = new[] { instance.BitsStored.ToString() } } },
                    { "00280102", new { vr = "US", Value = new[] { instance.HighBit.ToString() } } },
                    { "00280103", new { vr = "US", Value = new[] { instance.PixelRepresentation.ToString() } } },
                    { "00280004", new { vr = "CS", Value = new[] { instance.PhotometricInterpretation } } },
                    { "00280008", new { vr = "IS", Value = new[] { "1" } } },  // 默认为单帧图像
                    { "00200032", new { vr = "DS", Value = instance.ImagePositionPatient?.Split('\\') } },
                    { "00200037", new { vr = "DS", Value = instance.ImageOrientationPatient?.Split('\\') } },
                    { "00280030", new { vr = "DS", Value = instance.PixelSpacing?.Split('\\') } },
                    { "00280002", new { vr = "US", Value = new[] { instance.SamplesPerPixel.ToString() } } }
                };

                return Ok(new[] { metadata });
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检索实例元数据失败");
                return StatusCode(500, "Error retrieving instance metadata");
            }
        }

        #endregion

        #region WADO-RS 检索接口

        // WADO-RS: 检索检查
        [HttpGet("studies/{studyInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveStudy(string studyInstanceUid)
        {
            try
            {
                // 记录请求信息
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - 收到检查检索请求 - StudyUID: {StudyUID}, Accept: {Accept}",
                    studyInstanceUid, acceptHeader);

                // 获取检查下的所有实例
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                if (!instances.Any())
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到检查: {StudyUID}", studyInstanceUid);
                    return NotFound("Study not found");
                }

                // 按序列号和实例号排序
                instances = instances.OrderBy(i => int.Parse(
                        _repository.GetSeriesByStudyUid(studyInstanceUid)
                            .First(s => s.SeriesInstanceUid == i.SeriesInstanceUid)
                            .SeriesNumber ?? "0"))
                    .ThenBy(i => int.Parse(i.InstanceNumber ?? "0"))
                    .ToList();

                // 解析 Accept 头
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var mediaType = acceptParts.First().Trim();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));

                // 确定媒体类型
                if (mediaType == "*/*" || string.IsNullOrEmpty(mediaType))
                {
                    mediaType = AppDicomContentType;
                }

                // 从 multipart/related 中提取实际的媒体类型
                if (mediaType == "multipart/related" && typePart != null)
                {
                    var type = typePart.Split('=')[1].Trim('"', ' ');
                    if (type != "application/dicom")
                    {
                        return BadRequest("Unsupported media type. Only application/dicom is supported.");
                    }
                    mediaType = type;
                }

                // 确定传输语法
                DicomTransferSyntax targetTransferSyntax;
                if (transferSyntaxPart != null)
                {
                    var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (requestedSyntax == "*")
                    {
                        // 使用第一个实例的原始传输语法
                        var firstInstance = instances.First();
                        var filePath = Path.Combine(_settings.StoragePath, firstInstance.FilePath);
                        var dicomFile = await DicomFile.OpenAsync(filePath);
                        targetTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;
                        DicomLogger.Information("WADO", "DICOMweb - 使用原始传输语法: {TransferSyntax}", 
                            targetTransferSyntax.UID.Name);
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                        DicomLogger.Information("WADO", "DICOMweb - 使用请求的传输语法: {TransferSyntax}", 
                            targetTransferSyntax.UID.Name);
                    }
                }
                else
                {
                    // 默认使用显式 VR 小端 (1.2.840.10008.1.2.1)
                    targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                    DicomLogger.Information("WADO", "DICOMweb - 使用默认传输语法: ExplicitVRLittleEndian");
                }

                // 如果请求单个 DICOM 文件
                if (mediaType == AppDicomContentType && !acceptHeader.Contains("multipart/related"))
                {
                    // 使用第一个实例
                    var instance = instances.First();
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                        return NotFound("DICOM file not found");
                    }

                    // 读取DICOM文件
                    var dicomFile = await DicomFile.OpenAsync(filePath);

                    // 如果需要转换传输语法
                    if (dicomFile.FileMetaInfo.TransferSyntax != targetTransferSyntax)
                    {
                        DicomLogger.Information("WADO", 
                            "DICOMweb - 传输语法转换 - 当前: {CurrentSyntax}, 目标: {TargetSyntax}",
                            dicomFile.FileMetaInfo.TransferSyntax.UID.Name,
                            targetTransferSyntax.UID.Name);

                        var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, targetTransferSyntax);
                        dicomFile = transcoder.Transcode(dicomFile);
                    }

                    // 保存到内存流
                    using var dicomStream = new MemoryStream();
                    await dicomFile.SaveAsync(dicomStream);
                    var dicomBytes = dicomStream.ToArray();

                    // 设置响应头
                    Response.Headers["Content-Type"] = AppDicomContentType;
                    Response.Headers["Content-Length"] = dicomBytes.Length.ToString();
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                    return File(dicomBytes, AppDicomContentType, $"{studyInstanceUid}.dcm");
                }

                // 创建 multipart/related 响应
                var boundary = $"myboundary.{Guid.NewGuid():N}";
                var responseStream = new MemoryStream();
                var writer = new StreamWriter(responseStream);

                foreach (var instance in instances)
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Warning("WADO", "DICOMweb - 实例文件不存在: {FilePath}", filePath);
                        continue;
                    }

                    try
                    {
                        // 读取 DICOM 文件
                        var dicomFile = await DicomFile.OpenAsync(filePath);

                        // 如果需要转换传输语法
                        if (dicomFile.FileMetaInfo.TransferSyntax != targetTransferSyntax)
                        {
                            DicomLogger.Information("WADO", 
                                "DICOMweb - 传输语法转换 - 当前: {CurrentSyntax}, 目标: {TargetSyntax}",
                                dicomFile.FileMetaInfo.TransferSyntax.UID.Name,
                                targetTransferSyntax.UID.Name);

                            var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, targetTransferSyntax);
                            dicomFile = transcoder.Transcode(dicomFile);
                        }

                        // 写入分隔符和头部
                        await writer.WriteLineAsync($"--{boundary}");
                        await writer.WriteLineAsync("Content-Type: application/dicom");
                        await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{instance.SeriesInstanceUid}/instances/{instance.SopInstanceUid}");
                        await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // 将 DICOM 文件写入流
                        using var partStream = new MemoryStream();
                        await dicomFile.SaveAsync(partStream);
                        await partStream.CopyToAsync(responseStream);

                        // 写入换行
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        DicomLogger.Debug("WADO", "DICOMweb - 已添加实例到响应: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, "DICOMweb - 处理实例失败: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                        continue;
                    }
                }

                // 写入结束分隔符
                await writer.WriteLineAsync($"--{boundary}--");
                await writer.FlushAsync();

                // 准备返回数据
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // 设置响应头
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", "DICOMweb - 返回检查数据: {StudyUID}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}", 
                    studyInstanceUid, responseBytes.Length, targetTransferSyntax.UID.Name);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检查检索失败");
                return StatusCode(500, "Error retrieving study");
            }
        }

        // WADO-RS: 检索序列
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveSeries(
            string studyInstanceUid,
            string seriesInstanceUid)
        {
            try
            {
                // 记录请求信息
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - 收到序列检索请求 - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, acceptHeader);

                // 获取序列下的所有实例
                var instances = await Task.FromResult(_repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
                if (!instances.Any())
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到序列: {StudyUID}/{SeriesUID}", 
                        studyInstanceUid, seriesInstanceUid);
                    return NotFound("Series not found");
                }

                // 按实例号排序
                instances = instances.OrderBy(i => int.Parse(i.InstanceNumber ?? "0")).ToList();

                // 解析 Accept 头
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var mediaType = acceptParts.First().Trim();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));

                // 确定媒体类型
                if (mediaType == "*/*" || string.IsNullOrEmpty(mediaType))
                {
                    mediaType = AppDicomContentType;
                }

                // 从 multipart/related 中提取实际的媒体类型
                if (mediaType == "multipart/related" && typePart != null)
                {
                    var type = typePart.Split('=')[1].Trim('"', ' ');
                    if (type != "application/dicom")
                    {
                        return BadRequest("Unsupported media type. Only application/dicom is supported.");
                    }
                    mediaType = type;
                }

                // 确定传输语法
                DicomTransferSyntax targetTransferSyntax;
                if (transferSyntaxPart != null)
                {
                    var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (requestedSyntax == "*")
                    {
                        // 使用原始传输语法
                        var firstInstance = instances.First();
                        var filePath = Path.Combine(_settings.StoragePath, firstInstance.FilePath);
                        var dicomFile = await DicomFile.OpenAsync(filePath);
                        targetTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;
                        DicomLogger.Information("WADO", "DICOMweb - 使用原始传输语法: {TransferSyntax}", 
                            targetTransferSyntax.UID.Name);
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                        DicomLogger.Information("WADO", "DICOMweb - 使用请求的传输语法: {TransferSyntax}", 
                            targetTransferSyntax.UID.Name);
                    }
                }
                else
                {
                    // 默认使用显式 VR 小端 (1.2.840.10008.1.2.1)
                    targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                    DicomLogger.Information("WADO", "DICOMweb - 使用默认传输语法: ExplicitVRLittleEndian");
                }

                // 如果请求单个 DICOM 文件
                if (mediaType == AppDicomContentType && !acceptHeader.Contains("multipart/related"))
                {
                    // 使用第一个实例
                    var instance = instances.First();
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                        return NotFound("DICOM file not found");
                    }

                    // 读取DICOM文件
                    var dicomFile = await DicomFile.OpenAsync(filePath);

                    // 如果需要转换传输语法
                    if (dicomFile.FileMetaInfo.TransferSyntax != targetTransferSyntax)
                    {
                        DicomLogger.Information("WADO", 
                            "DICOMweb - 传输语法转换 - 当前: {CurrentSyntax}, 目标: {TargetSyntax}",
                            dicomFile.FileMetaInfo.TransferSyntax.UID.Name,
                            targetTransferSyntax.UID.Name);

                        var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, targetTransferSyntax);
                        dicomFile = transcoder.Transcode(dicomFile);
                    }

                    // 保存到内存流
                    using var dicomStream = new MemoryStream();
                    await dicomFile.SaveAsync(dicomStream);
                    var dicomBytes = dicomStream.ToArray();

                    // 设置响应头
                    Response.Headers["Content-Type"] = AppDicomContentType;
                    Response.Headers["Content-Length"] = dicomBytes.Length.ToString();
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                    return File(dicomBytes, AppDicomContentType, $"{seriesInstanceUid}.dcm");
                }

                // 创建 multipart/related 响应
                var boundary = $"myboundary.{Guid.NewGuid():N}";
                var responseStream = new MemoryStream();
                var writer = new StreamWriter(responseStream);

                foreach (var instance in instances)
                {
                    var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                    if (!System.IO.File.Exists(filePath))
                    {
                        DicomLogger.Warning("WADO", "DICOMweb - 实例文件不存在: {FilePath}", filePath);
                        continue;
                    }

                    try
                    {
                        // 读取 DICOM 文件
                        var dicomFile = await DicomFile.OpenAsync(filePath);

                        // 如果需要转换传输语法
                        if (dicomFile.FileMetaInfo.TransferSyntax != targetTransferSyntax)
                        {
                            DicomLogger.Information("WADO", 
                                "DICOMweb - 传输语法转换 - 当前: {CurrentSyntax}, 目标: {TargetSyntax}",
                                dicomFile.FileMetaInfo.TransferSyntax.UID.Name,
                                targetTransferSyntax.UID.Name);

                            var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, targetTransferSyntax);
                            dicomFile = transcoder.Transcode(dicomFile);
                        }

                        // 写入分隔符和头部
                        await writer.WriteLineAsync($"--{boundary}");
                        await writer.WriteLineAsync("Content-Type: application/dicom");
                        await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{instance.SopInstanceUid}");
                        await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // 将 DICOM 文件写入流
                        using var partStream = new MemoryStream();
                        await dicomFile.SaveAsync(partStream);
                        await partStream.CopyToAsync(responseStream);

                        // 写入换行
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        DicomLogger.Debug("WADO", "DICOMweb - 已添加实例到响应: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                    }
                    catch (Exception ex)
                    {
                        DicomLogger.Error("WADO", ex, "DICOMweb - 处理实例失败: {SopInstanceUid}", 
                            instance.SopInstanceUid);
                        continue;
                    }
                }

                // 写入结束分隔符
                await writer.WriteLineAsync($"--{boundary}--");
                await writer.FlushAsync();

                // 准备返回数据
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // 设置响应头
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", "DICOMweb - 返回序列数据: {SeriesUID}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}", 
                    seriesInstanceUid, responseBytes.Length, targetTransferSyntax.UID.Name);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 序列检索失败");
                return StatusCode(500, "Error retrieving series");
            }
        }

        // WADO-RS: 检索DICOM实例
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}")]
        [Produces("multipart/related", "application/dicom")]
        public async Task<IActionResult> RetrieveDicomInstance(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid)
        {
            try
            {
                // 记录请求信息
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - 收到实例检索请求 - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, SopUID: {SopUID}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, sopInstanceUid, acceptHeader);

                // 获取实例信息
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid && 
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                var currentTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;

                // 解析 Accept 头
                var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                var mediaType = acceptParts.First().Trim();
                var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                var typePart = acceptParts.FirstOrDefault(p => p.StartsWith("type=", StringComparison.OrdinalIgnoreCase));

                // 确定媒体类型
                if (mediaType == "*/*" || string.IsNullOrEmpty(mediaType))
                {
                    mediaType = AppDicomContentType;
                }

                // 从 multipart/related 中提取实际的媒体类型
                if (mediaType == "multipart/related" && typePart != null)
                {
                    var type = typePart.Split('=')[1].Trim('"', ' ');
                    mediaType = type;
                }

                // 确定传输语法
                DicomTransferSyntax targetTransferSyntax;
                if (transferSyntaxPart != null)
                {
                    var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                    if (requestedSyntax == "*")
                    {
                        // 使用原始传输语法
                        targetTransferSyntax = currentTransferSyntax;
                        DicomLogger.Information("WADO", "DICOMweb - 使用原始传输语法: {TransferSyntax}", 
                            currentTransferSyntax.UID.Name);
                    }
                    else
                    {
                        targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                        DicomLogger.Information("WADO", "DICOMweb - 使用请求的传输语法: {TransferSyntax}", 
                            targetTransferSyntax.UID.Name);
                    }
                }
                else
                {
                    // 默认使用显式 VR 小端 (1.2.840.10008.1.2.1)
                    targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                    DicomLogger.Information("WADO", "DICOMweb - 使用默认传输语法: ExplicitVRLittleEndian");
                }

                // 如果需要转换传输语法
                if (currentTransferSyntax != targetTransferSyntax)
                {
                    DicomLogger.Information("WADO", 
                        "DICOMweb - 传输语法转换 - 从: {CurrentSyntax} 到: {TargetSyntax}",
                        currentTransferSyntax.UID.Name,
                        targetTransferSyntax.UID.Name);

                    var transcoder = new DicomTranscoder(currentTransferSyntax, targetTransferSyntax);
                    dicomFile = transcoder.Transcode(dicomFile);
                }

                // 将 DICOM 文件保存到内存流
                using var memoryStream = new MemoryStream();
                await dicomFile.SaveAsync(memoryStream);
                var dicomBytes = memoryStream.ToArray();

                // 如果请求单个 DICOM 文件
                if (mediaType == AppDicomContentType && !acceptHeader.Contains("multipart/related"))
                {
                    Response.Headers["Content-Type"] = AppDicomContentType;
                    Response.Headers["Content-Length"] = dicomBytes.Length.ToString();
                    Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;
                    return File(dicomBytes, AppDicomContentType, $"{sopInstanceUid}.dcm");
                }

                // 默认返回 multipart/related 格式
                var boundary = $"boundary_{Guid.NewGuid():N}";
                var responseStream = new MemoryStream();
                var writer = new StreamWriter(responseStream, System.Text.Encoding.UTF8);

                // 写入第一个分隔符
                await writer.WriteLineAsync($"--{boundary}");

                // 写入 MIME 头部
                await writer.WriteLineAsync("Content-Type: application/dicom");
                await writer.WriteLineAsync($"Content-Length: {dicomBytes.Length}");
                await writer.WriteLineAsync($"Content-Location: /dicomweb/studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}");
                await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                await writer.WriteLineAsync();
                await writer.FlushAsync();

                // 写入 DICOM 数据
                await responseStream.WriteAsync(dicomBytes, 0, dicomBytes.Length);

                // 写入结束分隔符
                var endBoundary = $"\r\n--{boundary}--\r\n";
                var endBoundaryBytes = System.Text.Encoding.UTF8.GetBytes(endBoundary);
                await responseStream.WriteAsync(endBoundaryBytes, 0, endBoundaryBytes.Length);

                // 准备返回数据
                responseStream.Position = 0;
                var responseBytes = responseStream.ToArray();

                // 设置响应头
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", 
                    "DICOMweb - 返回DICOM实例: {SopInstanceUid}, Size: {Size} bytes, TransferSyntax: {TransferSyntax}", 
                    sopInstanceUid ?? string.Empty, responseBytes.Length, targetTransferSyntax.UID.Name ?? string.Empty);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", "检索DICOM实例失败: {Error}", ex.Message ?? string.Empty);
                return StatusCode(500, "Error retrieving DICOM instance");
            }
        }

        #endregion

        #region WADO-RS 缩略图接口

        // WADO-RS: 检索序列缩略图
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/thumbnail")]
        [Produces(JpegImageContentType)]
        public async Task<IActionResult> RetrieveSeriesThumbnail(
            string studyInstanceUid,
            string seriesInstanceUid,
            [FromQuery] int? size = null,
            [FromQuery] string? viewport = null)
        {
            try
            {
                // 从 viewport 参数解析尺寸（格式：width,height）
                if (viewport != null && size == null)
                {
                    var dimensions = viewport.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (dimensions.Length > 0 && int.TryParse(dimensions[0].Trim('%'), out int width))
                    {
                        size = width;
                        DicomLogger.Debug("WADO", "DICOMweb - 使用 viewport 参数: {SeriesInstanceUid}, Viewport: {Viewport}, 解析尺寸: {Size}", 
                            seriesInstanceUid, viewport, size);
                    }
                }
                else if (viewport != null && size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 同时提供了 size 和 viewport 参数，优先使用 size: {Size}, 忽略 viewport: {Viewport}", 
                        size, viewport);
                }
                else if (size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 使用 size 参数: {Size}", size);
                }
                else
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 使用默认尺寸: 128");
                }

                // 如果既没有 size 也没有 viewport，使用默认值
                var thumbnailSize = size ?? 128;

                // 获取序列中的第一个实例
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到序列: {SeriesInstanceUid}", seriesInstanceUid);
                    return NotFound("Series not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                DicomLogger.Debug("WADO", "DICOMweb - 生成序列缩略图: {SeriesInstanceUid}, Size: {Size}", 
                    seriesInstanceUid, thumbnailSize);
                var dicomImage = new DicomImage(dicomFile.Dataset);
                var renderedImage = dicomImage.RenderImage();

                // 转换为JPEG缩略图
                byte[] jpegBytes;
                using (var memoryStream = new MemoryStream())
                {
                    using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                        renderedImage.AsBytes(),
                        renderedImage.Width,
                        renderedImage.Height);

                    // 计算缩略图尺寸，保持宽高比
                    var ratio = Math.Min((double)thumbnailSize / image.Width, (double)thumbnailSize / image.Height);
                    var newWidth = (int)(image.Width * ratio);
                    var newHeight = (int)(image.Height * ratio);

                    DicomLogger.Debug("WADO", "DICOMweb - 序列缩略图尺寸: {SeriesInstanceUid}, Original: {OriginalWidth}x{OriginalHeight}, New: {NewWidth}x{NewHeight}", 
                        seriesInstanceUid, image.Width, image.Height, newWidth, newHeight);

                    // 调整图像大小
                    image.Mutate(x => x.Resize(newWidth, newHeight));

                    // 配置JPEG编码器选项 - 对缩略图使用较低的质量以减小文件大小
                    var encoder = new JpegEncoder
                    {
                        Quality = 75  // 缩略图使用较低的质量
                    };

                    // 保存为JPEG
                    await image.SaveAsJpegAsync(memoryStream, encoder);
                    jpegBytes = memoryStream.ToArray();
                }

                DicomLogger.Debug("WADO", "DICOMweb - 返回序列缩略图: {SeriesInstanceUid}, Size: {Size} bytes", 
                    seriesInstanceUid, jpegBytes.Length);

                return File(jpegBytes, JpegImageContentType, $"{seriesInstanceUid}_thumbnail.jpg");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检索序列缩略图失败");
                return StatusCode(500, "Error retrieving series thumbnail");
            }
        }

        // WADO-RS: 检索实例缩略图
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/thumbnail")]
        [Produces(JpegImageContentType)]
        public async Task<IActionResult> RetrieveThumbnail(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            [FromQuery] int? size = null,
            [FromQuery] string? viewport = null)
        {
            try
            {
                // 从 viewport 参数解析尺寸（格式：width,height）
                if (viewport != null && size == null)
                {
                    var dimensions = viewport.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (dimensions.Length > 0 && int.TryParse(dimensions[0].Trim('%'), out int width))
                    {
                        size = width;
                        DicomLogger.Debug("WADO", "DICOMweb - 使用 viewport 参数: {SopInstanceUid}, Viewport: {Viewport}, 解析尺寸: {Size}", 
                            sopInstanceUid, viewport, size);
                    }
                }
                else if (viewport != null && size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 同时提供了 size 和 viewport 参数，优先使用 size: {Size}, 忽略 viewport: {Viewport}", 
                        size, viewport);
                }
                else if (size != null)
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 使用 size 参数: {Size}", size);
                }
                else
                {
                    DicomLogger.Debug("WADO", "DICOMweb - 使用默认尺寸: 128");
                }

                // 如果既没有 size 也没有 viewport，使用默认值
                var thumbnailSize = size ?? 128;

                // 获取实例信息
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid && 
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOMweb - DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                DicomLogger.Information("WADO", "DICOMweb - 生成缩略图: {SopInstanceUid}, Size: {Size}", 
                    sopInstanceUid, thumbnailSize);
                var dicomImage = new DicomImage(dicomFile.Dataset);
                var renderedImage = dicomImage.RenderImage();

                // 转换为JPEG缩略图
                byte[] jpegBytes;
                using (var memoryStream = new MemoryStream())
                {
                    using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                        renderedImage.AsBytes(),
                        renderedImage.Width,
                        renderedImage.Height);

                    // 计算缩略图尺寸，保持宽高比
                    var ratio = Math.Min((double)thumbnailSize / image.Width, (double)thumbnailSize / image.Height);
                    var newWidth = (int)(image.Width * ratio);
                    var newHeight = (int)(image.Height * ratio);

                    DicomLogger.Information("WADO", "DICOMweb - 缩略图尺寸: {SopInstanceUid}, Original: {OriginalWidth}x{OriginalHeight}, New: {NewWidth}x{NewHeight}", 
                        sopInstanceUid, image.Width, image.Height, newWidth, newHeight);

                    // 调整图像大小
                    image.Mutate(x => x.Resize(newWidth, newHeight));

                    // 配置JPEG编码器选项 - 对缩略图使用较低的质量以减小文件大小
                    var encoder = new JpegEncoder
                    {
                        Quality = 75  // 缩略图使用较低的质量
                    };

                    // 保存为JPEG
                    await image.SaveAsJpegAsync(memoryStream, encoder);
                    jpegBytes = memoryStream.ToArray();
                }

                DicomLogger.Information("WADO", "DICOMweb - 返回缩略图: {SopInstanceUid}, Size: {Size} bytes", 
                    sopInstanceUid, jpegBytes.Length);

                return File(jpegBytes, JpegImageContentType, $"{sopInstanceUid}_thumbnail.jpg");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 检索缩略图失败");
                return StatusCode(500, "Error retrieving thumbnail");
            }
        }

        #endregion

        #region 辅助方法

        private DateTime? ParseDicomDate(string dicomDate)
        {
            if (string.IsNullOrEmpty(dicomDate)) return null;

            // 处理DICOM日期范围
            if (dicomDate.Contains('-'))
            {
                var dates = dicomDate.Split('-');
                if (dates.Length == 2)
                {
                    // 如果是日期范围，返回开始日期
                    dicomDate = dates[0];
                }
            }

            // 解析DICOM格式的日期 (YYYYMMDD)
            if (DateTime.TryParseExact(dicomDate, "yyyyMMdd", null, 
                System.Globalization.DateTimeStyles.None, out DateTime result))
            {
                return result;
            }

            return null;
        }

        private DateTime? GetStartDate(string? dicomDate)
        {
            if (string.IsNullOrEmpty(dicomDate)) return null;
            var dates = dicomDate.Split('-');
            return ParseDicomDate(dates[0]);
        }

        private DateTime? GetEndDate(string? dicomDate)
        {
            if (string.IsNullOrEmpty(dicomDate)) return null;
            var dates = dicomDate.Split('-');
            return ParseDicomDate(dates.Length > 1 ? dates[1] : dates[0]);
        }

        #endregion
    }
} 