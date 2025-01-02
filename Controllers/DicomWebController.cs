using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DicomSCP.Data;
using System.Text;

namespace DicomSCP.Controllers
{
    [ApiController]
    [Route("dicomweb")]
    [AllowAnonymous]  // 允许匿名访问
    public class DicomWebController : ControllerBase
    {
        private readonly ILogger<DicomWebController> _logger;
        private readonly DicomRepository _repository;

        public DicomWebController(
            DicomRepository repository,
            ILogger<DicomWebController> logger)
        {
            _repository = repository;
            _logger = logger;
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
                    { "00080060", new { vr = "CS", Value = new[] { s.Modality } } },  // Modality
                    { "0020000E", new { vr = "UI", Value = new[] { s.SeriesInstanceUid } } },  // SeriesInstanceUID
                    { "00200011", new { vr = "IS", Value = new[] { s.SeriesNumber } } },  // SeriesNumber
                    { "0008103E", new { vr = "LO", Value = new[] { s.SeriesDescription } } },  // SeriesDescription
                    { "00201209", new { vr = "IS", Value = new[] { s.NumberOfInstances.ToString() } } }  // NumberOfSeriesRelatedInstances
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
        public IActionResult SearchInstances(
            string studyInstanceUid,
            string seriesInstanceUid,
            [FromQuery] string? SOPInstanceUID = null)
        {
            try
            {
                var instances = _repository.GetInstancesByStudyUid(studyInstanceUid);
                
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
                    { "00080016", new { vr = "UI", Value = new[] { i.SopClassUid } } },  // SOPClassUID
                    { "00080018", new { vr = "UI", Value = new[] { i.SopInstanceUid } } },  // SOPInstanceUID
                    { "00200013", new { vr = "IS", Value = new[] { i.InstanceNumber } } },  // InstanceNumber
                    { "00280010", new { vr = "US", Value = new[] { i.Rows } } },  // Rows
                    { "00280011", new { vr = "US", Value = new[] { i.Columns } } },  // Columns
                    { "00280100", new { vr = "US", Value = new[] { i.BitsAllocated } } },  // BitsAllocated
                    { "00280101", new { vr = "US", Value = new[] { i.BitsStored } } },  // BitsStored
                    { "00280102", new { vr = "US", Value = new[] { i.HighBit } } },  // HighBit
                    { "00280103", new { vr = "US", Value = new[] { i.PixelRepresentation } } },  // PixelRepresentation
                    { "00280004", new { vr = "CS", Value = new[] { i.PhotometricInterpretation } } },  // PhotometricInterpretation
                    { "00280008", new { vr = "IS", Value = new[] { i.SamplesPerPixel.ToString() } } }  // SamplesPerPixel
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