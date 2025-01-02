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
using System.Text;
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
        private const string TransferSyntaxHeader = "transfer-syntax";
        private const string CharsetHeader = "charset";

        public DicomWebController(
            DicomRepository repository,
            IOptions<DicomSettings> settings,
            ILogger<DicomWebController> logger)
        {
            _repository = repository;
            _settings = settings.Value;
            _logger = logger;
        }

        private void AddDicomResponseHeaders(string contentType, string? transferSyntax = null)
        {
            Response.Headers.Append("Content-Type", contentType);
            if (!string.IsNullOrEmpty(transferSyntax))
            {
                Response.Headers.Append(TransferSyntaxHeader, transferSyntax);
            }
            Response.Headers.Append(CharsetHeader, "utf-8");
        }

        // WADO-RS: 检索DICOM实例
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}")]
        [Produces(AppDicomContentType)]
        public async Task<IActionResult> RetrieveDicomInstance(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            [FromQuery] string? transferSyntax = null)
        {
            try
            {
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

                // 如果指定了传输语法，进行转换
                if (!string.IsNullOrEmpty(transferSyntax))
                {
                    DicomLogger.Information("WADO", "DICOMweb - 请求传输语法转换: {TransferSyntax}", transferSyntax);
                    var requestedSyntax = DicomTransferSyntax.Parse(transferSyntax);
                    var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, requestedSyntax);
                    dicomFile = transcoder.Transcode(dicomFile);
                    DicomLogger.Information("WADO", "DICOMweb - 传输语法转换完成: {TransferSyntax}", transferSyntax);
                    AddDicomResponseHeaders(AppDicomContentType, transferSyntax);
                }
                else
                {
                    AddDicomResponseHeaders(AppDicomContentType, 
                        dicomFile.FileMetaInfo.TransferSyntax.UID.UID);
                }

                // 返回DICOM文件流
                using var memoryStream = new MemoryStream();
                await dicomFile.SaveAsync(memoryStream);
                memoryStream.Position = 0;
                
                DicomLogger.Information("WADO", "DICOMweb - 返回DICOM实例: {SopInstanceUid}, Size: {Size} bytes", 
                    sopInstanceUid, memoryStream.Length);
                
                return File(memoryStream.ToArray(), AppDicomContentType, $"{sopInstanceUid}.dcm");
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

        // WADO-RS: 检索帧
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/frames/{frameList}")]
        [Produces(AppDicomContentType)]
        public async Task<IActionResult> RetrieveFrames(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            string frameList,
            [FromQuery] string? transferSyntax = null)
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

                // 处理传输语法
                var targetSyntax = !string.IsNullOrEmpty(transferSyntax)
                    ? DicomTransferSyntax.Parse(transferSyntax)
                    : dicomFile.FileMetaInfo.TransferSyntax;

                newFile.FileMetaInfo.TransferSyntax = targetSyntax;

                // 如果需要转码
                if (targetSyntax != dicomFile.FileMetaInfo.TransferSyntax)
                {
                    DicomLogger.Information("WADO", "DICOMweb - 帧转换传输语法: {SopInstanceUid}, From: {FromSyntax}, To: {ToSyntax}", 
                        sopInstanceUid, dicomFile.FileMetaInfo.TransferSyntax.UID.Name, targetSyntax.UID.Name);
                    var transcoder = new DicomTranscoder(dicomFile.FileMetaInfo.TransferSyntax, targetSyntax);
                    newFile = transcoder.Transcode(newFile);
                }

                // 设置响应头
                AddDicomResponseHeaders(AppDicomContentType, targetSyntax.UID.UID);

                // 保存为DICOM文件并返回
                using var outputStream = new MemoryStream();
                await newFile.SaveAsync(outputStream);
                outputStream.Position = 0;

                DicomLogger.Information("WADO", "DICOMweb - 返回帧数据: {SopInstanceUid}, Frames: {Frames}, Size: {Size} bytes", 
                    sopInstanceUid, frameList, outputStream.Length);

                // 返回带文件名的DICOM文件
                return File(outputStream.ToArray(), AppDicomContentType, $"{sopInstanceUid}_frames_{frameList}.dcm");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索帧失败");
                return StatusCode(500, "Error retrieving frames");
            }
        }

        // WADO-RS: 检索缩略图
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances/{sopInstanceUid}/thumbnail")]
        [Produces(JpegImageContentType)]
        public async Task<IActionResult> RetrieveThumbnail(
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            [FromQuery] int? size = 128)  // 默认缩略图大小
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

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);
                DicomLogger.Information("WADO", "DICOMweb - 生成缩略图: {SopInstanceUid}, Size: {Size}", 
                    sopInstanceUid, size ?? 128);
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
                    var targetSize = size ?? 128;
                    var ratio = Math.Min((double)targetSize / image.Width, (double)targetSize / image.Height);
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
            [FromQuery(Name = "_offset")] int offset = 0,
            [FromQuery(Name = "_limit")] int limit = 100,
            [FromQuery(Name = "includefield")] string? includeField = null,
            [FromQuery(Name = "fuzzymatching")] bool fuzzyMatching = false)
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

                // 添加分页头信息
                Response.Headers.Append("X-Total-Count", studies.TotalCount.ToString());

                // 构建基础URL
                var urlBuilder = new StringBuilder();
                urlBuilder.Append(Request.Scheme).Append("://")
                         .Append(Request.Host)
                         .Append(Request.PathBase)
                         .Append(Request.Path);

                // 构建查询参数
                var queryParams = new List<string>();
                if (!string.IsNullOrEmpty(patientId)) queryParams.Add($"PatientID={Uri.EscapeDataString(patientId)}");
                if (!string.IsNullOrEmpty(patientName)) queryParams.Add($"PatientName={Uri.EscapeDataString(patientName)}");
                if (!string.IsNullOrEmpty(studyDate)) queryParams.Add($"StudyDate={Uri.EscapeDataString(studyDate)}");
                if (!string.IsNullOrEmpty(accessionNumber)) queryParams.Add($"AccessionNumber={Uri.EscapeDataString(accessionNumber)}");
                if (!string.IsNullOrEmpty(modalitiesInStudy)) queryParams.Add($"ModalitiesInStudy={Uri.EscapeDataString(modalitiesInStudy)}");
                if (!string.IsNullOrEmpty(includeField)) queryParams.Add($"includefield={Uri.EscapeDataString(includeField)}");
                if (fuzzyMatching) queryParams.Add("fuzzymatching=true");

                var baseUrl = urlBuilder.ToString();
                var links = new List<string>();

                // 添加first链接
                links.Add($"<{baseUrl}?_limit={limit}&_offset=0{(queryParams.Any() ? "&" + string.Join("&", queryParams) : "")}>; rel=\"first\"");

                // 添加prev链接
                if (offset > 0)
                {
                    links.Add($"<{baseUrl}?_limit={limit}&_offset={Math.Max(0, offset - limit)}{(queryParams.Any() ? "&" + string.Join("&", queryParams) : "")}>; rel=\"prev\"");
                }

                // 添加next链接
                if (offset + limit < studies.TotalCount)
                {
                    links.Add($"<{baseUrl}?_limit={limit}&_offset={offset + limit}{(queryParams.Any() ? "&" + string.Join("&", queryParams) : "")}>; rel=\"next\"");
                }

                // 添加last链接
                var lastOffset = ((studies.TotalCount - 1) / limit) * limit;
                links.Add($"<{baseUrl}?_limit={limit}&_offset={lastOffset}{(queryParams.Any() ? "&" + string.Join("&", queryParams) : "")}>; rel=\"last\"");

                Response.Headers.Append("Link", string.Join(", ", links));

                // 转换为DICOMweb JSON格式
                var result = studies.Items.Select(study =>
                {
                    var attributes = new Dictionary<string, object>();

                    // 根据includefield参数过滤属性
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00080020"))
                        attributes.Add("00080020", new { vr = "DA", Value = new[] { study.StudyDate } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00080050"))
                        attributes.Add("00080050", new { vr = "SH", Value = new[] { study.AccessionNumber } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00080061"))
                        attributes.Add("00080061", new { vr = "CS", Value = study.Modality?.Split('\\') });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00081030"))
                        attributes.Add("00081030", new { vr = "LO", Value = new[] { study.StudyDescription } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00100010"))
                        attributes.Add("00100010", new { vr = "PN", Value = new[] { study.PatientName } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00100020"))
                        attributes.Add("00100020", new { vr = "LO", Value = new[] { study.PatientId } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00100030"))
                        attributes.Add("00100030", new { vr = "DA", Value = new[] { study.PatientBirthDate } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00100040"))
                        attributes.Add("00100040", new { vr = "CS", Value = new[] { study.PatientSex } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("0020000D"))
                        attributes.Add("0020000D", new { vr = "UI", Value = new[] { study.StudyInstanceUid } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00201206"))
                        attributes.Add("00201206", new { vr = "IS", Value = new[] { study.NumberOfInstances.ToString() } });
                    if (string.IsNullOrEmpty(includeField) || includeField.Contains("all") || includeField.Contains("00201208"))
                        attributes.Add("00201208", new { vr = "IS", Value = new[] { "1" } });

                    return attributes;
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QIDO-RS 查询研究失败");
                return StatusCode(500, new Dictionary<string, object>
                {
                    { "00000900", new { vr = "CS", Value = new[] { "ProcessingFailure" } } },
                    { "00000901", new { vr = "US", Value = new[] { 500 } } },
                    { "00000902", new { vr = "LO", Value = new[] { ex.Message } } }
                });
            }
        }

        // QIDO-RS: 查询序列
        [HttpGet("studies/{studyInstanceUid}/series")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> SearchSeries(
            string studyInstanceUid,
            [FromQuery] string? SeriesInstanceUID = null,
            [FromQuery] string? Modality = null)
        {
            try
            {
                var series = await _repository.GetSeriesAsync(studyInstanceUid);

                // 应用过滤
                if (!string.IsNullOrEmpty(SeriesInstanceUID))
                {
                    series = series.Where(s => s.SeriesInstanceUid == SeriesInstanceUID);
                }
                if (!string.IsNullOrEmpty(Modality))
                {
                    series = series.Where(s => s.Modality == Modality);
                }

                // 转换为DICOMweb JSON格式
                var result = series.Select(s => new Dictionary<string, object>
                {
                    { "00080060", new { vr = "CS", Value = new[] { s.Modality } } },
                    { "0020000E", new { vr = "UI", Value = new[] { s.SeriesInstanceUid } } },
                    { "00200011", new { vr = "IS", Value = new[] { s.SeriesNumber } } },
                    { "0008103E", new { vr = "LO", Value = new[] { s.SeriesDescription } } },
                    { "00201209", new { vr = "IS", Value = new[] { s.NumberOfInstances.ToString() } } }
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QIDO-RS 查询序列失败");
                return StatusCode(500, ex.Message);
            }
        }

        // QIDO-RS: 查询实例
        [HttpGet("studies/{studyInstanceUid}/series/{seriesInstanceUid}/instances")]
        [Produces("application/dicom+json")]
        public async Task<IActionResult> SearchInstances(
            string studyInstanceUid,
            string seriesInstanceUid,
            [FromQuery] string? SOPInstanceUID = null)
        {
            try
            {
                var instances = await Task.FromResult(_repository.GetInstancesByStudyUid(studyInstanceUid));
                
                // 应用序列过滤
                instances = instances.Where(i => i.SeriesInstanceUid == seriesInstanceUid);

                // 应用SOP实例过滤
                if (!string.IsNullOrEmpty(SOPInstanceUID))
                {
                    instances = instances.Where(i => i.SopInstanceUid == SOPInstanceUID);
                }

                // 转换为DICOMweb JSON格式
                var result = instances.Select(i => new Dictionary<string, object>
                {
                    { "00080016", new { vr = "UI", Value = new[] { i.SopClassUid } } },
                    { "00080018", new { vr = "UI", Value = new[] { i.SopInstanceUid } } },
                    { "00200013", new { vr = "IS", Value = new[] { i.InstanceNumber } } },
                    { "00280010", new { vr = "US", Value = new[] { i.Rows } } },
                    { "00280011", new { vr = "US", Value = new[] { i.Columns } } },
                    { "00280100", new { vr = "US", Value = new[] { i.BitsAllocated } } },
                    { "00280101", new { vr = "US", Value = new[] { i.BitsStored } } },
                    { "00280102", new { vr = "US", Value = new[] { i.HighBit } } },
                    { "00280103", new { vr = "US", Value = new[] { i.PixelRepresentation } } },
                    { "00280004", new { vr = "CS", Value = new[] { i.PhotometricInterpretation } } },
                    { "00280008", new { vr = "IS", Value = new[] { i.SamplesPerPixel.ToString() } } }
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QIDO-RS 查询实例失败");
                return StatusCode(500, ex.Message);
            }
        }

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
    }
} 