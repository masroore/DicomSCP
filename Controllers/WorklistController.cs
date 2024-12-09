using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;
using DicomSCP.Services;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorklistController : ControllerBase
{
    private readonly WorklistRepository _repository;

    public WorklistController(WorklistRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<WorklistItem>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? patientId = null,
        [FromQuery] string? patientName = null,
        [FromQuery] string? accessionNumber = null,
        [FromQuery] string? modality = null,
        [FromQuery] string? scheduledDate = null)
    {
        try
        {
            // 验证分页参数
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 10;
            if (pageSize > 100) pageSize = 100;  // 限制最大页面大小

            var result = await _repository.GetPagedAsync(
                page,
                pageSize,
                patientId,
                patientName,
                accessionNumber,
                modality,
                scheduledDate);

            // 计算年龄
            foreach (var item in result.Items)
            {
                if (!string.IsNullOrEmpty(item.PatientBirthDate))
                {
                    var birthYear = int.Parse(item.PatientBirthDate.Substring(0, 4));
                    var today = DateTime.Today;
                    item.Age = today.Year - birthYear;
                }
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取Worklist列表失败");
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<WorklistItem>> GetById(string id)
    {
        try
        {
            var item = await _repository.GetByIdAsync(id);
            if (item == null)
            {
                return NotFound();
            }

            // 计算年龄
            if (!string.IsNullOrEmpty(item.PatientBirthDate))
            {
                var birthYear = int.Parse(item.PatientBirthDate.Substring(0, 4));
                var today = DateTime.Today;
                item.Age = today.Year - birthYear;
            }

            return Ok(item);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 获取Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpPost]
    public async Task<ActionResult<WorklistItem>> Create(WorklistItem item)
    {
        try
        {
            DicomLogger.Information("Api", "[API] 正在创建Worklist项目: {@Item}", item);

            // 验证必填字段
            if (string.IsNullOrEmpty(item.PatientId))
                return BadRequest("患者ID不能为空");
            if (string.IsNullOrEmpty(item.PatientName))
                return BadRequest("患者姓名不能为空");
            if (!item.Age.HasValue || item.Age < 0 || item.Age > 150)
                return BadRequest("年龄必须在0-150岁之间");
            if (string.IsNullOrEmpty(item.AccessionNumber))
                return BadRequest("检查号不能为空");
            if (string.IsNullOrEmpty(item.Modality))
                return BadRequest("检查类型不能为空");
            if (string.IsNullOrEmpty(item.ScheduledDateTime))
                return BadRequest("预约时间不能为空");
            if (string.IsNullOrEmpty(item.ScheduledAET))
                return BadRequest("预约AE Title不能为空");

            // 验证状态值
            var validStatuses = new[] { "SCHEDULED", "IN_PROGRESS", "COMPLETED", "DISCONTINUED" };
            if (!validStatuses.Contains(item.Status))
            {
                item.Status = "SCHEDULED";  // 如果状态无效，默认为已预约
            }

            // 生成新的 WorklistId
            item.WorklistId = Guid.NewGuid().ToString("N");

            // 生成 StudyInstanceUID
            if (string.IsNullOrEmpty(item.StudyInstanceUid))
            {
                // 使用时间戳（精确到毫秒）作为基础，加上4位随机数
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                var random = new Random().Next(1000, 9999);
                item.StudyInstanceUid = $"2.25.{timestamp}{random}";
            }

            // 根据年龄计算出生日期
            var today = DateTime.Today;
            var birthYear = today.Year - item.Age.Value;
            item.PatientBirthDate = $"{birthYear}0101";  // YYYYMMDD 格式

            var worklistId = await _repository.CreateAsync(item);
            item.WorklistId = worklistId;

            DicomLogger.Information("Api", "[API] 成功创建Worklist项目 - ID: {WorklistId}", worklistId);
            return Ok(item);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 创建Worklist项目失败");
            return StatusCode(500, $"创建失败: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, WorklistItem item)
    {
        try
        {
            if (id != item.WorklistId)
            {
                return BadRequest();
            }

            // 获取原有记录，保留 StudyInstanceUID
            var existingItem = await _repository.GetByIdAsync(id);
            if (existingItem == null)
            {
                return NotFound();
            }
            
            // 保持原有的 StudyInstanceUID
            item.StudyInstanceUid = existingItem.StudyInstanceUid;

            // 验证年龄
            if (!item.Age.HasValue || item.Age < 0 || item.Age > 150)
                return BadRequest("年龄必须在0-150岁之间");

            // 根据年龄计算出生日期
            var today = DateTime.Today;
            var birthYear = today.Year - item.Age.Value;
            item.PatientBirthDate = $"{birthYear}0101";  // YYYYMMDD 格式

            var success = await _repository.UpdateAsync(item);
            if (!success)
            {
                return NotFound();
            }

            DicomLogger.Information("Api", "[API] 成功更新Worklist项目 - ID: {WorklistId}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 更新Worklist项目失败 - ID: {WorklistId}", id);
            return StatusCode(500, $"更新失败: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            var success = await _repository.DeleteAsync(id);
            if (!success)
            {
                return NotFound();
            }
            return NoContent();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 删除Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "删除失败");
        }
    }
} 