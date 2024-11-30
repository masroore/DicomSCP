using Dapper;
using Microsoft.Data.Sqlite;
using DicomSCP.Models;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using DicomSCP.Data;

namespace DicomSCP.Data;

public class WorklistRepository : BaseRepository
{
    public WorklistRepository(IOptions<DicomSettings> settings, ILogger<WorklistRepository> logger)
        : base(settings.Value.ConnectionString, logger)
    {
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 创建 Worklist 表
        var sql = @"
            CREATE TABLE IF NOT EXISTS Worklist (
                WorklistId TEXT PRIMARY KEY,
                AccessionNumber TEXT NOT NULL,
                PatientId TEXT NOT NULL,
                PatientName TEXT NOT NULL,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                StudyInstanceUid TEXT NOT NULL,
                StudyDescription TEXT,
                Modality TEXT NOT NULL,
                ScheduledAET TEXT NOT NULL,
                ScheduledDateTime TEXT NOT NULL,
                ScheduledStationName TEXT,
                ScheduledProcedureStepID TEXT,
                ScheduledProcedureStepDescription TEXT,
                RequestedProcedureID TEXT,
                RequestedProcedureDescription TEXT,
                ReferringPhysicianName TEXT,
                Status TEXT NOT NULL DEFAULT 'SCHEDULED',
                CreateTime TEXT NOT NULL,
                UpdateTime TEXT NOT NULL,
                BodyPartExamined TEXT,
                ReasonForRequest TEXT
            )";

        connection.Execute(sql);
    }

    public async Task<IEnumerable<WorklistItem>> GetAllAsync()
    {
        LogDebug("正在查询所有Worklist项目");
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT * FROM Worklist 
            ORDER BY ScheduledDateTime DESC";

        return await connection.QueryAsync<WorklistItem>(sql);
    }

    public async Task<WorklistItem?> GetByIdAsync(string worklistId)
    {
        LogDebug("正在查询Worklist项目 - WorklistId: {WorklistId}", worklistId);
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT * FROM Worklist WHERE WorklistId = @WorklistId";

        var item = await connection.QueryFirstOrDefaultAsync<WorklistItem>(sql, new { WorklistId = worklistId });
        
        // 确保日期时间格式正确
        if (item != null && !string.IsNullOrEmpty(item.ScheduledDateTime))
        {
            // 尝试解析并重新格式化日期时间
            if (DateTime.TryParse(item.ScheduledDateTime, out var dateTime))
            {
                item.ScheduledDateTime = dateTime.ToString("yyyy-MM-ddTHH:mm");
            }
        }

        return item;
    }

    public async Task<string> CreateAsync(WorklistItem item)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            
            // WorklistId 应该由控制器生成并传入
            if (string.IsNullOrEmpty(item.WorklistId))
            {
                throw new ArgumentException("WorklistId不能为空");
            }

            // 设置创建和更新时间
            item.CreateTime = DateTime.UtcNow;
            item.UpdateTime = item.CreateTime;

            LogInformation("正在创建Worklist项目 - WorklistId: {WorklistId}", item.WorklistId);

            var sql = @"
                INSERT INTO Worklist (
                    WorklistId, AccessionNumber, PatientId, PatientName, 
                    PatientBirthDate, PatientSex, StudyInstanceUid, StudyDescription,
                    Modality, ScheduledAET, ScheduledDateTime, ScheduledStationName,
                    ScheduledProcedureStepID, ScheduledProcedureStepDescription,
                    RequestedProcedureID, RequestedProcedureDescription,
                    ReferringPhysicianName, Status, CreateTime, UpdateTime,
                    BodyPartExamined, ReasonForRequest
                ) VALUES (
                    @WorklistId, @AccessionNumber, @PatientId, @PatientName,
                    @PatientBirthDate, @PatientSex, @StudyInstanceUid, @StudyDescription,
                    @Modality, @ScheduledAET, @ScheduledDateTime, @ScheduledStationName,
                    @ScheduledProcedureStepID, @ScheduledProcedureStepDescription,
                    @RequestedProcedureID, @RequestedProcedureDescription,
                    @ReferringPhysicianName, @Status, @CreateTime, @UpdateTime,
                    @BodyPartExamined, @ReasonForRequest
                )";

            await connection.ExecuteAsync(sql, item);
            LogInformation("成功创建Worklist项目 - WorklistId: {WorklistId}", item.WorklistId);
            return item.WorklistId;
        }
        catch (Exception ex)
        {
            LogError(ex, "创建Worklist项目失败");
            throw;
        }
    }

    public async Task<bool> UpdateAsync(WorklistItem item)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            
            // 更新时间
            item.UpdateTime = DateTime.UtcNow;

            LogInformation("正在更新Worklist项目 - WorklistId: {WorklistId}", item.WorklistId);

            var sql = @"
                UPDATE Worklist 
                SET AccessionNumber = @AccessionNumber,
                    PatientId = @PatientId,
                    PatientName = @PatientName,
                    PatientBirthDate = @PatientBirthDate,
                    PatientSex = @PatientSex,
                    StudyInstanceUid = @StudyInstanceUid,
                    StudyDescription = @StudyDescription,
                    Modality = @Modality,
                    ScheduledAET = @ScheduledAET,
                    ScheduledDateTime = @ScheduledDateTime,
                    ScheduledStationName = @ScheduledStationName,
                    ScheduledProcedureStepID = @ScheduledProcedureStepID,
                    ScheduledProcedureStepDescription = @ScheduledProcedureStepDescription,
                    RequestedProcedureID = @RequestedProcedureID,
                    RequestedProcedureDescription = @RequestedProcedureDescription,
                    ReferringPhysicianName = @ReferringPhysicianName,
                    Status = @Status,
                    UpdateTime = @UpdateTime,
                    BodyPartExamined = @BodyPartExamined,
                    ReasonForRequest = @ReasonForRequest
                WHERE WorklistId = @WorklistId";

            var rowsAffected = await connection.ExecuteAsync(sql, item);
            
            if (rowsAffected > 0)
            {
                LogInformation("成功更新Worklist项目 - WorklistId: {WorklistId}", item.WorklistId);
            }
            else
            {
                LogWarning("未找到要更新的Worklist项目 - WorklistId: {WorklistId}", item.WorklistId);
            }
            
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新Worklist项目失败 - WorklistId: {WorklistId}", item.WorklistId);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string worklistId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            
            LogInformation("正在删除Worklist项目 - WorklistId: {WorklistId}", worklistId);
            
            var sql = "DELETE FROM Worklist WHERE WorklistId = @WorklistId";

            var rowsAffected = await connection.ExecuteAsync(sql, new { WorklistId = worklistId });
            
            if (rowsAffected > 0)
            {
                LogInformation("成功删除Worklist项目 - WorklistId: {WorklistId}", worklistId);
            }
            else
            {
                LogWarning("未找到要删除的Worklist项目 - WorklistId: {WorklistId}", worklistId);
            }
            
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "删除Worklist项目失败 - WorklistId: {WorklistId}", worklistId);
            throw;
        }
    }
} 