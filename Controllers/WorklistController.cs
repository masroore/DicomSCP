using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Data;
using Microsoft.Extensions.Logging;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorklistController : ControllerBase
{
    private readonly WorklistRepository _repository;
    private readonly ILogger<WorklistController> _logger;

    public WorklistController(WorklistRepository repository, ILogger<WorklistController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<WorklistItem>>> GetAll()
    {
        try
        {
            var items = await _repository.GetAllAsync();
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.PatientBirthDate))
                {
                    var birthYear = int.Parse(item.PatientBirthDate.Substring(0, 4));
                    var today = DateTime.Today;
                    item.Age = today.Year - birthYear;
                }
            }
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取Worklist列表失败");
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
            _logger.LogError(ex, "获取Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "获取数据失败");
        }
    }

    [HttpPost]
    public async Task<ActionResult<WorklistItem>> Create(WorklistItem item)
    {
        try
        {
            _logger.LogInformation("正在创建Worklist项目: {@Item}", item);

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

            // 生成新的 WorklistId
            item.WorklistId = Guid.NewGuid().ToString("N");

            // 根据年龄计算出生日期
            var today = DateTime.Today;
            var birthYear = today.Year - item.Age.Value;
            item.PatientBirthDate = $"{birthYear}0101";  // YYYYMMDD 格式

            var worklistId = await _repository.CreateAsync(item);
            item.WorklistId = worklistId;

            _logger.LogInformation("成功创建Worklist项目 - ID: {WorklistId}", worklistId);
            return Ok(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建Worklist项目失败");
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
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "更新失败");
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
            _logger.LogError(ex, "删除Worklist项目失败 - ID: {Id}", id);
            return StatusCode(500, "删除失败");
        }
    }
} 