using Dapper;
using DicomSCP.Models;
using FellowOakDicom;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using DicomSCP.Data;

namespace DicomSCP.Data;

public class DicomRepository : BaseRepository, IDisposable
{
    private readonly ConcurrentQueue<(DicomDataset Dataset, string FilePath)> _dataQueue = new();
    private readonly SemaphoreSlim _processSemaphore = new(1, 1);
    private readonly Timer _processTimer;
    private readonly int _batchSize;
    private readonly TimeSpan _maxWaitTime = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _minWaitTime = TimeSpan.FromSeconds(5);
    private readonly Stopwatch _performanceTimer = new();
    private DateTime _lastProcessTime = DateTime.UtcNow;
    private bool _initialized;

    private static class SqlQueries
    {
        public const string InsertPatient = @"
            INSERT OR IGNORE INTO Patients 
            (PatientId, PatientName, PatientBirthDate, PatientSex, CreateTime)
            VALUES (@PatientId, @PatientName, @PatientBirthDate, @PatientSex, @CreateTime)";

        public const string InsertStudy = @"
            INSERT OR IGNORE INTO Studies 
            (StudyInstanceUid, PatientId, StudyDate, StudyTime, StudyDescription, AccessionNumber, CreateTime)
            VALUES (@StudyInstanceUid, @PatientId, @StudyDate, @StudyTime, @StudyDescription, @AccessionNumber, @CreateTime)";

        public const string InsertSeries = @"
            INSERT OR IGNORE INTO Series 
            (SeriesInstanceUid, StudyInstanceUid, Modality, SeriesNumber, SeriesDescription, CreateTime)
            VALUES (@SeriesInstanceUid, @StudyInstanceUid, @Modality, @SeriesNumber, @SeriesDescription, @CreateTime)";

        public const string InsertInstance = @"
            INSERT OR IGNORE INTO Instances 
            (SopInstanceUid, SeriesInstanceUid, SopClassUid, InstanceNumber, FilePath, CreateTime)
            VALUES (@SopInstanceUid, @SeriesInstanceUid, @SopClassUid, @InstanceNumber, @FilePath, @CreateTime)";

        public const string CreateWorklistTable = @"
            CREATE TABLE IF NOT EXISTS Worklist (
                WorklistId TEXT PRIMARY KEY,
                AccessionNumber TEXT,
                PatientId TEXT,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                StudyInstanceUid TEXT,
                StudyDescription TEXT,
                Modality TEXT,
                ScheduledAET TEXT,
                ScheduledDateTime TEXT,
                ScheduledStationName TEXT,
                ScheduledProcedureStepID TEXT,
                ScheduledProcedureStepDescription TEXT,
                RequestedProcedureID TEXT,
                RequestedProcedureDescription TEXT,
                ReferringPhysicianName TEXT,
                Status TEXT DEFAULT 'SCHEDULED',
                BodyPartExamined TEXT,
                ReasonForRequest TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdateTime DATETIME DEFAULT CURRENT_TIMESTAMP
            )";

        public const string QueryWorklist = @"
            SELECT * FROM Worklist 
            WHERE (@PatientId IS NULL OR PatientId LIKE @PatientId)
            AND (@AccessionNumber IS NULL OR AccessionNumber LIKE @AccessionNumber)
            AND (@ScheduledDateTime IS NULL OR ScheduledDateTime LIKE @ScheduledDateTime)
            AND (@Modality IS NULL OR Modality = @Modality)
            AND (@ScheduledStationName IS NULL OR ScheduledStationName = @ScheduledStationName)
            AND Status = 'SCHEDULED'";

        public const string CreatePatientsTable = @"
            CREATE TABLE IF NOT EXISTS Patients (
                PatientId TEXT PRIMARY KEY,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                CreateTime DATETIME
            )";

        public const string CreateStudiesTable = @"
            CREATE TABLE IF NOT EXISTS Studies (
                StudyInstanceUid TEXT PRIMARY KEY,
                PatientId TEXT,
                StudyDate TEXT,
                StudyTime TEXT,
                StudyDescription TEXT,
                AccessionNumber TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(PatientId) REFERENCES Patients(PatientId)
            )";

        public const string CreateSeriesTable = @"
            CREATE TABLE IF NOT EXISTS Series (
                SeriesInstanceUid TEXT PRIMARY KEY,
                StudyInstanceUid TEXT,
                Modality TEXT,
                SeriesNumber TEXT,
                SeriesDescription TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(StudyInstanceUid) REFERENCES Studies(StudyInstanceUid)
            )";

        public const string CreateInstancesTable = @"
            CREATE TABLE IF NOT EXISTS Instances (
                SopInstanceUid TEXT PRIMARY KEY,
                SeriesInstanceUid TEXT,
                SopClassUid TEXT,
                InstanceNumber TEXT,
                FilePath TEXT,
                CreateTime DATETIME,
                FOREIGN KEY(SeriesInstanceUid) REFERENCES Series(SeriesInstanceUid)
            )";

        public const string CreateUsersTable = @"
            CREATE TABLE IF NOT EXISTS Users (
                Username TEXT PRIMARY KEY,
                Password TEXT NOT NULL
            )";

        public const string InitializeAdminUser = @"
            INSERT OR IGNORE INTO Users (Username, Password) 
            VALUES ('admin', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=')";
    }

    public DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger)
        : base(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"), logger)
    {
        _batchSize = configuration.GetValue<int>("DicomSettings:BatchSize", 50);

        // 初始化数据库
        InitializeDatabase();
        _processTimer = new Timer(async _ => await ProcessQueueAsync(), null, _minWaitTime, _minWaitTime);
    }

    private async Task ProcessQueueAsync()
    {
        if (_dataQueue.IsEmpty) return;

        var queueSize = _dataQueue.Count;
        var waitTime = DateTime.UtcNow - _lastProcessTime;

        // 优化处理时机判断
        if (queueSize >= _batchSize || // 队列达到批处理大小
            (queueSize > 0 && waitTime >= _maxWaitTime) || // 等待时间达到上限
            (queueSize >= 10 && waitTime >= _minWaitTime)) // 积累一定数量且达到最小等待时间
        {
            await ProcessBatchWithRetryAsync();
        }
    }

    private async Task ProcessBatchWithRetryAsync()
    {
        if (!await _processSemaphore.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            return;
        }

        List<(DicomDataset Dataset, string FilePath)> batchItems = new();

        try
        {
            _performanceTimer.Restart();
            var batchSize = Math.Min(_dataQueue.Count, _batchSize);
            
            // 一次性收集批处理数据
            while (batchItems.Count < batchSize && _dataQueue.TryDequeue(out var item))
            {
                batchItems.Add(item);
            }

            if (batchItems.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var now = DateTime.UtcNow;
                var patients = new List<Patient>();
                var studies = new List<Study>();
                var series = new List<Series>();
                var instances = new List<Instance>();

                // 预分配容量以提高性能
                patients.Capacity = batchItems.Count;
                studies.Capacity = batchItems.Count;
                series.Capacity = batchItems.Count;
                instances.Capacity = batchItems.Count;

                foreach (var (dataset, filePath) in batchItems)
                {
                    ExtractDicomData(dataset, filePath, now, patients, studies, series, instances);
                }

                // 批量插入数据
                await connection.ExecuteAsync(SqlQueries.InsertPatient, patients, transaction);
                await connection.ExecuteAsync(SqlQueries.InsertStudy, studies, transaction);
                await connection.ExecuteAsync(SqlQueries.InsertSeries, series, transaction);
                await connection.ExecuteAsync(SqlQueries.InsertInstance, instances, transaction);

                await transaction.CommitAsync();

                _performanceTimer.Stop();
                _lastProcessTime = DateTime.UtcNow;

                LogInformation(
                    "批量处理完成 - 数量: {Count}, 耗时: {Time}ms, 队列剩余: {Remaining}, 平均耗时: {AvgTime:F2}ms/条", 
                    batchItems.Count,
                    _performanceTimer.ElapsedMilliseconds,
                    _dataQueue.Count,
                    _performanceTimer.ElapsedMilliseconds / (double)batchItems.Count);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                LogError(ex, "批量处理失败 - 批次大小: {Count}, 将重新入队", batchItems.Count);
                
                // 错误时重新入队，保持原有顺序
                foreach (var item in batchItems)
                {
                    _dataQueue.Enqueue(item);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "处理批次时发生异常");
        }
        finally
        {
            _processSemaphore.Release();
        }
    }

    private void InitializeDatabase()
    {
        if (_initialized) return;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        // 检查是否已存在表
        var tableExists = connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Studies'") > 0;

        try
        {
            // 创建所有表
            connection.Execute(SqlQueries.CreatePatientsTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateStudiesTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateSeriesTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateInstancesTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateWorklistTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateUsersTable, transaction: transaction);
            connection.Execute(SqlQueries.InitializeAdminUser, transaction: transaction);

            transaction.Commit();
            _initialized = true;
            if (!tableExists)
            {
                LogInformation("数据库表首次初始化完成");
            }
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            LogError(ex, "初始化数据库表失败");
            throw;
        }
    }

    private void ExtractDicomData(
        DicomDataset dataset, 
        string filePath, 
        DateTime now,
        List<Patient> patients,
        List<Study> studies,
        List<Series> series,
        List<Instance> instances)
    {
        var patientId = dataset.GetSingleValue<string>(DicomTag.PatientID);
        var studyInstanceUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
        var seriesInstanceUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);

        patients.Add(new Patient
        {
            PatientId = patientId,
            PatientName = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty),
            PatientBirthDate = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientBirthDate, string.Empty),
            PatientSex = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientSex, string.Empty),
            CreateTime = now
        });

        studies.Add(new Study
        {
            StudyInstanceUid = studyInstanceUid,
            PatientId = patientId,
            StudyDate = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty),
            StudyTime = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty),
            StudyDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDescription, string.Empty),
            AccessionNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            CreateTime = now
        });

        series.Add(new Series
        {
            SeriesInstanceUid = seriesInstanceUid,
            StudyInstanceUid = studyInstanceUid,
            Modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty),
            SeriesNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesNumber, string.Empty),
            SeriesDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesDescription, string.Empty),
            CreateTime = now
        });

        instances.Add(new Instance
        {
            SopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
            SeriesInstanceUid = seriesInstanceUid,
            SopClassUid = dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
            InstanceNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.InstanceNumber, string.Empty),
            FilePath = filePath,
            CreateTime = now
        });
    }

    private void HandleFailedBatch(List<(DicomDataset Dataset, string FilePath)> failedItems)
    {
        // 可以实现失败处理逻辑，比如：
        // 1. 写入错误日志文件
        // 2. 存入特定的错误表
        // 3. 发送告警通知
        // 4. 放入重试队列等
    }

    public async Task SaveDicomDataAsync(DicomDataset dataset, string filePath)
    {
        _dataQueue.Enqueue((dataset, filePath));
        
        // 当队列达到批处理大小的80%时，主动触发处理
        if (_dataQueue.Count >= _batchSize * 0.8)
        {
            await ProcessQueueAsync();
        }
    }

    public void Dispose()
    {
        _processTimer?.Dispose();
        _processSemaphore?.Dispose();
        
        // 处理剩余队列中的数据
        if (!_dataQueue.IsEmpty)
        {
            ProcessBatchWithRetryAsync().Wait();
        }
    }

    public async Task<IEnumerable<dynamic>> GetAllStudiesWithPatientInfoAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.StudyInstanceUid, s.PatientId, s.StudyDate, s.StudyTime, 
                   s.StudyDescription, s.AccessionNumber, s.CreateTime,
                   p.PatientName, p.PatientSex, p.PatientBirthDate,
                   (SELECT Modality FROM Series WHERE StudyInstanceUid = s.StudyInstanceUid LIMIT 1) as Modality
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            ORDER BY s.CreateTime DESC";

        return await connection.QueryAsync(sql);
    }

    public async Task<IEnumerable<Series>> GetSeriesByStudyUidAsync(string studyUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT s.*, 
                   (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances,
                   (SELECT Modality FROM Studies WHERE StudyInstanceUid = s.StudyInstanceUid) as StudyModality
            FROM Series s
            WHERE s.StudyInstanceUid = @StudyUid
            ORDER BY CAST(s.SeriesNumber as INTEGER)";

        return await connection.QueryAsync<Series>(sql, new { StudyUid = studyUid });
    }

    public async Task<bool> ValidateUserAsync(string username, string password)
    {
        using var connection = new SqliteConnection(_connectionString);
        var hashedPassword = HashPassword(password);
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = @Username AND Password = @Password",
            new { Username = username, Password = hashedPassword }
        );
        return count > 0;
    }

    public async Task<bool> ChangePasswordAsync(string username, string newPassword)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var hashedPassword = HashPassword(newPassword);
        
        var sql = @"
            UPDATE Users 
            SET Password = @Password 
            WHERE Username = @Username";
        
        var result = await connection.ExecuteAsync(sql, new { 
            Username = username, 
            Password = hashedPassword 
        });

        return result > 0;
    }

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }

    public async Task DeleteStudyAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
  
        try
        {
            // 先检查是否存在相关记录
            var instanceCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除与 Series 相关的 Instances
            await connection.ExecuteAsync(
                "DELETE FROM Instances WHERE SeriesInstanceUid IN (SELECT SeriesInstanceUid FROM Series WHERE StudyInstanceUid = @StudyInstanceUid)",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除与 Study 相关的 Series
            await connection.ExecuteAsync(
                "DELETE FROM Series WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            // 删除 Study
            await connection.ExecuteAsync(
                "DELETE FROM Studies WHERE StudyInstanceUid = @StudyInstanceUid",
                new { StudyInstanceUid = studyInstanceUid },
                transaction: transaction
            );
  
            await transaction.CommitAsync();
            LogInformation("成功删除检查 - StudyInstanceUID: {StudyInstanceUid}, 删除实例数: {InstanceCount}", 
                studyInstanceUid, instanceCount);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            LogError(ex, "删除检查失败 - 检查实例UID: {StudyInstanceUid}", studyInstanceUid);
            throw;
        }
    }

    public async Task<Study?> GetStudyAsync(string studyInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        var sql = @"
            SELECT s.*, p.PatientName, p.PatientId, p.PatientSex, p.PatientBirthDate
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            WHERE s.StudyInstanceUid = @StudyInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Study>(
            sql,
            new { StudyInstanceUid = studyInstanceUid }
        );
    }

    public async Task<IEnumerable<Instance>> GetSeriesInstancesAsync(string seriesInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = @"
            SELECT * FROM Instances 
            WHERE SeriesInstanceUid = @SeriesInstanceUid 
            ORDER BY CAST(InstanceNumber as INTEGER)";
        
        return await connection.QueryAsync<Instance>(sql, new { SeriesInstanceUid = seriesInstanceUid });
    }

    public async Task<Instance?> GetInstanceAsync(string sopInstanceUid)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT * FROM Instances WHERE SopInstanceUid = @SopInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Instance>(
            sql, 
            new { SopInstanceUid = sopInstanceUid }
        );
    }

    public List<WorklistItem> GetWorklistItems(
        string patientId,
        string accessionNumber,
        string scheduledDateTime,
        string modality,
        string scheduledStationName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"SELECT * FROM Worklist 
                WHERE (@PatientId IS NULL OR @PatientId = '' OR PatientId LIKE @PatientId)
                AND (@AccessionNumber IS NULL OR @AccessionNumber = '' OR AccessionNumber LIKE @AccessionNumber)
                AND (@ScheduledDateTime IS NULL OR @ScheduledDateTime = '' OR ScheduledDateTime LIKE @ScheduledDateTime)
                AND (@Modality IS NULL OR @Modality = '' OR Modality = @Modality)
                AND (@ScheduledStationName IS NULL OR @ScheduledStationName = '' OR ScheduledStationName = @ScheduledStationName)
                AND Status = 'SCHEDULED'";

            LogDebug("执行工作列表查询 - SQL: {Sql}, 参数: {@Parameters}", sql, new
            {
                PatientId = patientId,
                AccessionNumber = accessionNumber,
                ScheduledDateTime = scheduledDateTime,
                Modality = modality,
                ScheduledStationName = scheduledStationName
            });

            var items = connection.Query<WorklistItem>(sql,
                new
                {
                    PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                    AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                    ScheduledDateTime = string.IsNullOrEmpty(scheduledDateTime) ? "" : $"%{scheduledDateTime}%",
                    Modality = string.IsNullOrEmpty(modality) ? "" : modality,
                    ScheduledStationName = string.IsNullOrEmpty(scheduledStationName) ? "" : scheduledStationName
                });

            var result = items?.ToList() ?? new List<WorklistItem>();
            LogInformation("工作列表查询完成 - 返回记录数: {Count}", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "工作列表查询失败");
            throw;
        }
    }

    public List<Study> GetStudies(string patientId, string patientName, string accessionNumber, string studyDate)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT s.*, p.PatientName, p.PatientSex, p.PatientBirthDate,
                       (SELECT Modality FROM Series WHERE StudyInstanceUid = s.StudyInstanceUid LIMIT 1) as Modality
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                WHERE (@PatientId IS NULL OR @PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName IS NULL OR @PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber IS NULL OR @AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StudyDate IS NULL OR @StudyDate = '' OR s.StudyDate LIKE @StudyDate)
                ORDER BY s.CreateTime DESC";

            LogDebug("执行检查查询 - SQL: {Sql}, 参数: {@Parameters}", sql, new
            {
                PatientId = patientId,
                PatientName = patientName,
                AccessionNumber = accessionNumber,
                StudyDate = studyDate
            });

            var studies = connection.Query<Study>(sql, new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StudyDate = string.IsNullOrEmpty(studyDate) ? "" : $"{studyDate}%"
            });

            var result = studies?.ToList() ?? new List<Study>();
            LogInformation("检查查询完成 - 返回记录数: {Count}", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "检查查询失败");
            return new List<Study>();
        }
    }

    public List<Series> GetSeriesByStudyUid(string studyInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT s.*, 
                       (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances
                FROM Series s
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(s.SeriesNumber as INTEGER)";

            LogDebug("执行序列查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var series = connection.Query<Series>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = series?.ToList() ?? new List<Series>();
            LogInformation("序列查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "序列查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            return new List<Series>();
        }
    }

    public List<Instance> GetInstancesBySeriesUid(string studyInstanceUid, string seriesInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid 
                AND i.SeriesInstanceUid = @SeriesInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行图像查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                sql, studyInstanceUid, seriesInstanceUid);

            var instances = connection.Query<Instance>(sql, new 
            { 
                StudyInstanceUid = studyInstanceUid,
                SeriesInstanceUid = seriesInstanceUid
            });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("图像查询完成 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, seriesInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "图像查询失败 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                studyInstanceUid, seriesInstanceUid);
            return new List<Instance>();
        }
    }

    public IEnumerable<Instance> GetInstancesByStudyUid(string studyInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行实例查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var instances = connection.Query<Instance>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("实例查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "实例查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            return new List<Instance>();
        }
    }
}