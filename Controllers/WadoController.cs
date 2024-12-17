using DicomSCP.Data;
using DicomSCP.Configuration;
using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace DicomSCP.Controllers
{
    [Route("wado")]
    [AllowAnonymous]
    public class WadoController : ControllerBase
    {
        private readonly DicomRepository _dicomRepository;
        private readonly DicomSettings _settings;
        private const string AppDicomContentType = "application/dicom";
        private const string JpegImageContentType = "image/jpeg";

        public WadoController(
            DicomRepository dicomRepository,
            IOptions<DicomSettings> settings)
        {
            _dicomRepository = dicomRepository;
            _settings = settings.Value;
        }

        [HttpGet]
        public async Task<IActionResult> GetStudyInstances(
            [FromQuery] string? requestType,
            [FromQuery] string studyUID,
            [FromQuery] string seriesUID, 
            [FromQuery] string objectUID,
            [FromQuery] string? contentType = default,
            [FromQuery] string? transferSyntax = default,
            [FromQuery] string? anonymize = default)
        {
            // 验证必需参数
            if (string.IsNullOrEmpty(studyUID) || string.IsNullOrEmpty(seriesUID) || string.IsNullOrEmpty(objectUID))
            {
                DicomLogger.Warning("WADO", "缺少必需参数");
                return BadRequest("Missing required parameters");
            }

            DicomLogger.Information("WADO", "收到WADO请求 - StudyUID: {StudyUID}, SeriesUID: {SeriesUID}, ObjectUID: {ObjectUID}, ContentType: {ContentType}, TransferSyntax: {TransferSyntax}",
                studyUID, seriesUID, objectUID, contentType ?? "default", transferSyntax ?? "default");

            // 验证请求类型 (必需参数)
            if (requestType?.ToUpper() != "WADO")
            {
                DicomLogger.Warning("WADO", "无效的请求类型: {RequestType}", requestType ?? "null");
                return BadRequest("Invalid requestType - WADO is required");
            }

            try
            {
                // 从数据库获取实例信息
                var instance = await _dicomRepository.GetInstanceAsync(objectUID);
                if (instance == null)
                {
                    DicomLogger.Warning("WADO", "未找到实例: {ObjectUID}", objectUID);
                    return NotFound("Instance not found");
                }

                // 构建完整的文件路径
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    DicomLogger.Error("WADO", "DICOM文件不存在: {FilePath}", filePath);
                    return NotFound("DICOM file not found");
                }

                // 读取DICOM文件
                var dicomFile = await DicomFile.OpenAsync(filePath);

                // 确定最终的内容类型
                string finalContentType = PickFinalContentType(contentType, dicomFile);
                DicomLogger.Information("WADO", "最终内容类型: {ContentType}", finalContentType);
                
                // 根据请求内容类型返回
                if (finalContentType == JpegImageContentType)
                {
                    // 如果请求匿名化，先处理匿名化
                    if (anonymize == "yes")
                    {
                        DicomLogger.Information("WADO", "执行匿名化处理");
                        dicomFile = AnonymizeDicomFile(dicomFile);
                    }

                    // 返回JPEG
                    var dicomImage = new DicomImage(dicomFile.Dataset);
                    
                    // 渲染图像 - 直接使用默认参数
                    var renderedImage = dicomImage.RenderImage();
                    
                    // 转换为JPEG
                    using var memoryStream = new MemoryStream();
                    using (var image = Image.LoadPixelData<Rgba32>(
                        renderedImage.AsBytes(), 
                        renderedImage.Width, 
                        renderedImage.Height))
                    {
                        // 配置JPEG编码器选项
                        var encoder = new JpegEncoder
                        {
                            Quality = 90  // 设置JPEG质量
                        };

                        // 保存为JPEG
                        await image.SaveAsJpegAsync(memoryStream, encoder);
                    }

                    var jpegBytes = memoryStream.ToArray();
                    DicomLogger.Information("WADO", "成功返回JPEG图像 - Size: {Size} bytes", jpegBytes.Length);

                    // 设置文件名为 SOP Instance UID
                    var contentDisposition = new System.Net.Mime.ContentDisposition
                    {
                        FileName = $"{objectUID}.jpg",
                        Inline = false  // 使用 attachment 方式下载
                    };
                    Response.Headers["Content-Disposition"] = contentDisposition.ToString();

                    return File(jpegBytes, JpegImageContentType);
                }
                else
                {
                    // 如果请求匿名化，先处理匿名化
                    if (anonymize == "yes")
                    {
                        DicomLogger.Information("WADO", "执行匿名化处理");
                        dicomFile = AnonymizeDicomFile(dicomFile);
                    }

                    // 返回DICOM（包括传输语法转换）
                    var dicomBytes = await GetDicomBytes(dicomFile, transferSyntax);
                    DicomLogger.Information("WADO", "成功返回DICOM文件 - Size: {Size} bytes", dicomBytes.Length);
                    
                    // 设置文件名为 SOP Instance UID
                    var contentDisposition = new System.Net.Mime.ContentDisposition
                    {
                        FileName = $"{objectUID}.dcm",
                        Inline = false  // 使用 attachment 方式下载
                    };
                    Response.Headers["Content-Disposition"] = contentDisposition.ToString();
                    
                    return File(dicomBytes, AppDicomContentType);
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Error("WADO", ex, "处理WADO请求时发生错误");
                return StatusCode(500, $"Error processing image: {ex.Message}");
            }
        }

        private string PickFinalContentType(string? contentType, DicomFile dicomFile)
        {
            // 如果没有指定内容类型，根据图像类型选择默认值
            if (string.IsNullOrEmpty(contentType))
            {
                // 获取帧数
                var numberOfFrames = dicomFile.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
                // 多帧图像默认返回 DICOM，单帧图像默认返回 JPEG
                return numberOfFrames > 1 ? AppDicomContentType : JpegImageContentType;
            }

            return contentType;
        }

        private async Task<byte[]> GetDicomBytes(DicomFile dicomFile, string? transferSyntax)
        {
            // 如果需要转换传输语法
            if (!string.IsNullOrEmpty(transferSyntax))
            {
                try 
                {
                    var currentSyntax = dicomFile.Dataset.InternalTransferSyntax;
                    var requestedSyntax = GetRequestedTransferSyntax(transferSyntax);

                    DicomLogger.Information("WADO", "传输语法 - 当前: {CurrentSyntax}, 请求: {RequestedSyntax}", 
                        currentSyntax.UID.Name,
                        requestedSyntax.UID.Name);

                    if (currentSyntax != requestedSyntax)
                    {
                        try
                        {
                            var transcoder = new DicomTranscoder(currentSyntax, requestedSyntax);
                            dicomFile = transcoder.Transcode(dicomFile);
                            DicomLogger.Information("WADO", "已转换传输语法为: {NewSyntax}", requestedSyntax.UID.Name);
                        }
                        catch (Exception ex)
                        {
                            DicomLogger.Error("WADO", ex, "传输语法转换失败: {TransferSyntax}", transferSyntax);
                            // 如果转换失败，返回错误而不是使用原始语法
                            throw new InvalidOperationException($"无法转换到请求的传输语法: {transferSyntax}", ex);
                        }
                    }
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    DicomLogger.Warning("WADO", ex, "无效的传输语法请求: {TransferSyntax}", transferSyntax);
                    // 如果是解析错误，继续使用原始语法
                }
            }

            using var ms = new MemoryStream();
            await dicomFile.SaveAsync(ms);
            return ms.ToArray();
        }

        private DicomTransferSyntax GetRequestedTransferSyntax(string syntax)
        {
            try
            {
                // 尝试直接解析传输语法 UID
                return DicomTransferSyntax.Parse(syntax);
            }
            catch
            {
                // 如果解析失败，使用常见的传输语法 UID 映射
                return syntax switch
                {
                    // Uncompressed
                    "1.2.840.10008.1.2" => DicomTransferSyntax.ImplicitVRLittleEndian,
                    "1.2.840.10008.1.2.1" => DicomTransferSyntax.ExplicitVRLittleEndian,
                    "1.2.840.10008.1.2.2" => DicomTransferSyntax.ExplicitVRBigEndian,
                    
                    // JPEG Baseline
                    "1.2.840.10008.1.2.4.50" => DicomTransferSyntax.JPEGProcess1,
                    "1.2.840.10008.1.2.4.51" => DicomTransferSyntax.JPEGProcess2_4,
                    
                    // JPEG Lossless
                    "1.2.840.10008.1.2.4.57" => DicomTransferSyntax.JPEGProcess14,
                    "1.2.840.10008.1.2.4.70" => DicomTransferSyntax.JPEGProcess14SV1,
                    
                    // JPEG 2000
                    "1.2.840.10008.1.2.4.90" => DicomTransferSyntax.JPEG2000Lossless,
                    "1.2.840.10008.1.2.4.91" => DicomTransferSyntax.JPEG2000Lossy,
                    
                    // JPEG-LS
                    "1.2.840.10008.1.2.4.80" => DicomTransferSyntax.JPEGLSLossless,
                    "1.2.840.10008.1.2.4.81" => DicomTransferSyntax.JPEGLSNearLossless,
                    
                    // RLE
                    "1.2.840.10008.1.2.5" => DicomTransferSyntax.RLELossless,
                    
                    // Default to Explicit VR Little Endian if unknown
                    _ => DicomTransferSyntax.ExplicitVRLittleEndian
                };
            }
        }

        private DicomFile AnonymizeDicomFile(DicomFile dicomFile)
        {
            // 克隆数据集
            var newDataset = dicomFile.Dataset.Clone();
            
            // 基本标签匿名化
            newDataset.AddOrUpdate(DicomTag.PatientName, "ANONYMOUS");
            newDataset.AddOrUpdate(DicomTag.PatientID, "ANONYMOUS");
            newDataset.AddOrUpdate(DicomTag.PatientBirthDate, "19000101");
            
            // 移除敏感标签
            newDataset.Remove(DicomTag.PatientAddress);
            newDataset.Remove(DicomTag.PatientTelephoneNumbers);
            newDataset.Remove(DicomTag.PatientMotherBirthName);
            newDataset.Remove(DicomTag.OtherPatientIDsSequence);  // 修正标签名
            newDataset.Remove(DicomTag.OtherPatientNames);
            newDataset.Remove(DicomTag.PatientComments);
            newDataset.Remove(DicomTag.InstitutionName);
            newDataset.Remove(DicomTag.ReferringPhysicianName);
            newDataset.Remove(DicomTag.PerformingPhysicianName);
            newDataset.Remove(DicomTag.NameOfPhysiciansReadingStudy);
            newDataset.Remove(DicomTag.OperatorsName);
            
            // 修改研究和序列描述
            newDataset.AddOrUpdate(DicomTag.StudyDescription, "ANONYMOUS");
            newDataset.AddOrUpdate(DicomTag.SeriesDescription, "ANONYMOUS");
            
            // 创建新的 DicomFile
            var anonymizedFile = new DicomFile(newDataset);
            
            // 复制文件元信息
            anonymizedFile.FileMetaInfo.TransferSyntax = dicomFile.FileMetaInfo.TransferSyntax;
            anonymizedFile.FileMetaInfo.MediaStorageSOPClassUID = dicomFile.FileMetaInfo.MediaStorageSOPClassUID;
            anonymizedFile.FileMetaInfo.MediaStorageSOPInstanceUID = dicomFile.FileMetaInfo.MediaStorageSOPInstanceUID;
            
            return anonymizedFile;
        }
    }
} 