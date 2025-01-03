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
using System.Globalization;
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
                _logger.LogError(ex, "QIDO-RS 查询研究失败");
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
                _logger.LogError(ex, "QIDO-RS 查询序列失败");
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
                _logger.LogError(ex, "QIDO-RS 查询实例失败");
                return StatusCode(500, "Error searching instances");
            }
        }

        #endregion

        #region WADO-RS 检索接口

        // WADO-RS: 检索序列
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}")]
        [Produces("multipart/related")]
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
                var instances = _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid);
                if (!instances.Any())
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 未找到序列: {StudyUID}/{SeriesUID}", 
                        studyInstanceUid, seriesInstanceUid);
                    return NotFound("Series not found");
                }

                // 按实例号排序
                instances = instances.OrderBy(i => int.Parse(i.InstanceNumber ?? "0")).ToList();

                // 创建 multipart 响应
                var boundary = $"myboundary.{Guid.NewGuid():N}";
                Response.Headers.Append("Content-Type", $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");

                // 创建内存流
                var memoryStream = new MemoryStream();
                var writer = new StreamWriter(memoryStream);

                // 从 Accept 头中解析传输语法
                DicomTransferSyntax targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                if (!string.IsNullOrEmpty(acceptHeader))
                {
                    var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                    var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                    if (transferSyntaxPart != null)
                    {
                        var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                        if (requestedSyntax != "*")
                        {
                            try
                            {
                                targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                                DicomLogger.Information("WADO", "DICOMweb - 从Accept头解析传输语法: {TransferSyntax}", requestedSyntax);
                            }
                            catch (Exception ex)
                            {
                                DicomLogger.Warning("WADO", ex, "DICOMweb - 无效的传输语法: {TransferSyntax}", requestedSyntax);
                            }
                        }
                    }
                }

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
                        await writer.WriteLineAsync($"transfer-syntax: {targetTransferSyntax.UID.UID}");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();

                        // 将 DICOM 文件写入流
                        using var partStream = new MemoryStream();
                        await dicomFile.SaveAsync(partStream);
                        await partStream.CopyToAsync(memoryStream);

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

                DicomLogger.Information("WADO", "DICOMweb - 返回序列数据: {SeriesUID}, Size: {Size} bytes", 
                    seriesInstanceUid, memoryStream.Length);

                return File(memoryStream.ToArray(), $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "DICOMweb - 序列检索失败");
                return StatusCode(500, "Error retrieving series");
            }
        }

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
                    DicomLogger.Warning("WADO", "未找到序列: {SeriesInstanceUid}", seriesInstanceUid);
                    return NotFound("Series not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOM文件不存在: {FilePath}", filePath);
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
                _logger.LogError(ex, "检索序列缩略图失败");
                return StatusCode(500, "Error retrieving series thumbnail");
            }
        }

        // WADO-RS: 检索DICOM实例
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}")]
        [Produces("multipart/related")]
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
                    DicomLogger.Warning("WADO", "未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                var currentTransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;

                // 从 Accept 头中解析传输语法
                DicomTransferSyntax targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                if (!string.IsNullOrEmpty(acceptHeader))
                {
                    var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                    var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                    if (transferSyntaxPart != null)
                    {
                        var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                        if (requestedSyntax != "*")
                        {
                            try
                            {
                                targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                                DicomLogger.Information("WADO", "DICOMweb - 从Accept头解析传输语法: {TransferSyntax}", requestedSyntax);
                            }
                            catch (Exception ex)
                            {
                                DicomLogger.Warning("WADO", ex, "DICOMweb - 无效的传输语法: {TransferSyntax}", requestedSyntax);
                            }
                        }
                    }
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

                // 创建 multipart 响应
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
                    sopInstanceUid, responseBytes.Length, targetTransferSyntax.UID.Name);

                return File(responseBytes, $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索DICOM实例失败");
                return StatusCode(500, "Error retrieving DICOM instance");
            }
        }

        // WADO-RS: 检索元数据
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/metadata")]
        [Produces(DicomJsonContentType)]
        public async Task<IActionResult> RetrieveMetadata(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid)
        {
            try
            {
                // 获取实例信息
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid && 
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    _logger.LogWarning("未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    _logger.LogError("DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件并提取元数据
                var dicomFile = await DicomFile.OpenAsync(filePath);
                DicomLogger.Information("WADO", "DICOMweb - 读取元数据: {SopInstanceUid}", sopInstanceUid);
                var metadata = new Dictionary<string, object>();

                // 转换为DICOMweb JSON格式
                foreach (var tag in dicomFile.Dataset)
                {
                    var tagKey = tag.Tag.ToString("X8", CultureInfo.InvariantCulture);
                    var values = tag.ValueRepresentation.IsString
                        ? new[] { tag.ToString() }
                        : tag.ToString().Split('\\');

                    metadata[tagKey] = new
                    {
                        vr = tag.ValueRepresentation.Code,
                        Value = values
                    };
                }

                DicomLogger.Information("WADO", "DICOMweb - 返回元数据: {SopInstanceUid}, Tags: {TagCount}", 
                    sopInstanceUid, metadata.Count);
                return Ok(metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索元数据失败");
                return StatusCode(500, "Error retrieving metadata");
            }
        }

        // WADO-RS: 检索缩略图
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
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid && 
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    _logger.LogWarning("未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    _logger.LogError("DICOM文件不存在: {FilePath}", filePath);
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
                _logger.LogError(ex, "检索缩略图失败");
                return StatusCode(500, "Error retrieving thumbnail");
            }
        }

        // WADO-RS: 检索帧
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/frames/{frameList}")]
        [Produces("multipart/related")]
        public async Task<IActionResult> RetrieveFrames(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            string frameList)
        {
            try
            {
                // 记录请求信息
                var acceptHeader = Request.Headers["Accept"].ToString();
                DicomLogger.Information("WADO", "DICOMweb - 收到帧检索请求 - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, SopUID: {SopUID}, Frames: {Frames}, Accept: {Accept}",
                    studyInstanceUid, seriesInstanceUid, sopInstanceUid, frameList, acceptHeader);

                // 获取实例信息
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                var instance = instances.FirstOrDefault(i => 
                    i.SeriesInstanceUid == seriesInstanceUid && 
                    i.SopInstanceUid == sopInstanceUid);

                if (instance == null)
                {
                    _logger.LogWarning("未找到实例: {SopInstanceUid}", sopInstanceUid);
                    return NotFound("Instance not found");
                }

                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    _logger.LogError("DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 解析帧号
                var frameNumbers = frameList.Split(',').Select(int.Parse).ToList();
                DicomLogger.Information("WADO", "DICOMweb - 请求帧: {SopInstanceUid}, Frames: {Frames}", 
                    sopInstanceUid, frameList);
                
                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                var pixelData = DicomPixelData.Create(dicomFile.Dataset);

                // 验证帧号
                if (frameNumbers.Any(f => f < 1 || f > pixelData.NumberOfFrames))
                {
                    DicomLogger.Warning("WADO", "DICOMweb - 无效的帧号: {SopInstanceUid}, Frames: {Frames}, MaxFrames: {MaxFrames}", 
                        sopInstanceUid, frameList, pixelData.NumberOfFrames);
                    return BadRequest("Invalid frame number");
                }

                // 创建新的DICOM数据集
                var newDataset = dicomFile.Dataset.Clone();
                var newPixelData = DicomPixelData.Create(newDataset, true);

                // 添加指定的帧
                foreach (var frameNumber in frameNumbers)
                {
                    var frameBuffer = pixelData.GetFrame(frameNumber - 1);
                    newPixelData.AddFrame(frameBuffer);
                }

                DicomLogger.Information("WADO", "DICOMweb - 提取帧完成: {SopInstanceUid}, Frames: {Frames}", 
                    sopInstanceUid, frameList);

                // 更新帧数
                newDataset.AddOrUpdate(DicomTag.NumberOfFrames, frameNumbers.Count);

                // 创建新的DICOM文件
                var newFile = new DicomFile(newDataset);

                // 从 Accept 头中解析传输语法
                DicomTransferSyntax targetTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
                if (!string.IsNullOrEmpty(acceptHeader))
                {
                    var acceptParts = acceptHeader.Split(';').Select(p => p.Trim()).ToList();
                    var transferSyntaxPart = acceptParts.FirstOrDefault(p => p.StartsWith("transfer-syntax=", StringComparison.OrdinalIgnoreCase));
                    if (transferSyntaxPart != null)
                    {
                        var requestedSyntax = transferSyntaxPart.Split('=')[1].Trim('"', ' ');
                        if (requestedSyntax != "*")
                        {
                            try
                            {
                                targetTransferSyntax = DicomTransferSyntax.Parse(requestedSyntax);
                                DicomLogger.Information("WADO", "DICOMweb - 从Accept头解析传输语法: {TransferSyntax}", requestedSyntax);
                            }
                            catch (Exception ex)
                            {
                                DicomLogger.Warning("WADO", ex, "DICOMweb - 无效的传输语法: {TransferSyntax}", requestedSyntax);
                            }
                        }
                    }
                }

                newFile.FileMetaInfo.TransferSyntax = targetTransferSyntax;

                // 如果需要转码
                if (targetTransferSyntax != dicomFile.FileMetaInfo.TransferSyntax)
                {
                    DicomLogger.Information("WADO", "DICOMweb - 帧转换传输语法: {SopInstanceUid}, From: {FromSyntax}, To: {ToSyntax}", 
                        sopInstanceUid, dicomFile.FileMetaInfo.TransferSyntax.UID.Name, targetTransferSyntax.UID.Name);
                    var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, targetTransferSyntax);
                    newFile = transcoder.Transcode(newFile);
                }

                // 创建 multipart 响应
                var boundary = $"boundary_{Guid.NewGuid():N}";

                // 保存为DICOM文件并返回
                using var outputStream = new MemoryStream();
                await newFile.SaveAsync(outputStream);
                var responseBytes = outputStream.ToArray();

                // 设置响应头
                Response.Headers.Clear();
                Response.Headers["Content-Type"] = $"multipart/related; type=\"application/dicom\"; boundary=\"{boundary}\"";
                Response.Headers["Content-Length"] = responseBytes.Length.ToString();
                Response.Headers["transfer-syntax"] = targetTransferSyntax.UID.UID;

                DicomLogger.Information("WADO", "DICOMweb - 返回帧数据: {SopInstanceUid}, Frames: {Frames}, Size: {Size} bytes", 
                    sopInstanceUid, frameList, responseBytes.Length);

                return File(responseBytes, AppDicomContentType, $"{sopInstanceUid}_frames_{frameList}.dcm");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索帧失败");
                return StatusCode(500, "Error retrieving frames");
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