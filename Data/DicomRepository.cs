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
using System.IO;
using System.Text.Json;
using System.Data;

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
            (StudyInstanceUid, PatientId, StudyDate, StudyTime, StudyDescription, AccessionNumber, Modality, CreateTime)
            VALUES (@StudyInstanceUid, @PatientId, @StudyDate, @StudyTime, @StudyDescription, @AccessionNumber, @Modality, @CreateTime)";

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
                Modality TEXT,
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

        public const string CreatePrintJobsTable = @"
            CREATE TABLE IF NOT EXISTS PrintJobs (
                JobId TEXT PRIMARY KEY,
                FilmSessionId TEXT,
                FilmBoxId TEXT,
                ImageBoxId TEXT,
                CallingAE TEXT,
                Status TEXT,
                ImagePath TEXT,
                PrinterName TEXT,
                ErrorMessage TEXT,
                -- 患者信息
                PatientId TEXT,
                PatientName TEXT,
                AccessionNumber TEXT,
                -- 打印参数
                FilmSize TEXT,
                FilmOrientation TEXT,
                FilmLayout TEXT,
                MagnificationType TEXT,
                BorderDensity TEXT,
                EmptyImageDensity TEXT,
                MinDensity TEXT,
                MaxDensity TEXT,
                TrimValue TEXT,
                ConfigurationInfo TEXT,
                CreateTime DATETIME,
                UpdateTime DATETIME
            )";
    }

    public DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger)
        : base(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"), logger)
    {
        _batchSize = configuration.GetValue<int>("DicomSettings:BatchSize", 50);

        // 初始化数据库
        Task.Run(async () =>
        {
            try
            {
                await InitializeDatabase();
            }
            catch (Exception ex)
            {
                LogError(ex, "初始化数据库失败");
            }
        }).GetAwaiter().GetResult();

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
                    "批量处理完成 - 数量: {Count}, 耗时: {Time}ms, 队剩余: {Remaining}, 平均耗时: {AvgTime:F2}ms/条", 
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

    private async Task InitializeDatabase()
    {
        if (_initialized) return;

        // 确保数据库目录存在
        var dbPath = Path.GetDirectoryName(_connectionString.Replace("Data Source=", "").Trim());
        if (!string.IsNullOrEmpty(dbPath))
        {
            Directory.CreateDirectory(dbPath);
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        // 检查是否已存在表
        var tableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Studies'");

        try
        {
            // 创建所有表
            await connection.ExecuteAsync(SqlQueries.CreatePatientsTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateStudiesTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateSeriesTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateInstancesTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateWorklistTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.CreateUsersTable, transaction: transaction);
            await connection.ExecuteAsync(SqlQueries.InitializeAdminUser, transaction: transaction);
            
            // 创建打印任务表
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS PrintJobs (
                    JobId TEXT PRIMARY KEY,
                    FilmSessionId TEXT,
                    FilmBoxId TEXT,
                    ImageBoxId TEXT,
                    CallingAE TEXT,
                    Status TEXT,
                    ImagePath TEXT,
                    PrinterName TEXT,
                    ErrorMessage TEXT,
                    -- 患者信息
                    PatientId TEXT,
                    PatientName TEXT,
                    AccessionNumber TEXT,
                    -- 打印参数
                    FilmSize TEXT,
                    FilmOrientation TEXT,
                    FilmLayout TEXT,
                    MagnificationType TEXT,
                    BorderDensity TEXT,
                    EmptyImageDensity TEXT,
                    MinDensity TEXT,
                    MaxDensity TEXT,
                    TrimValue TEXT,
                    ConfigurationInfo TEXT,
                    CreateTime DATETIME,
                    UpdateTime DATETIME
                )", transaction: transaction);

            await transaction.CommitAsync();
            _initialized = true;
            if (tableExists == 0)
            {
                LogInformation("数据库表首次初始化完成");
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
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
            Modality = GetStudyModality(dataset),
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

    private string GetStudyModality(DicomDataset dataset)
    {
        // 首先尝试从ModalitiesInStudy获取
        try
        {
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                var modalities = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                if (modalities != null && modalities.Length > 0)
                {
                    return string.Join("\\", modalities.Where(m => !string.IsNullOrEmpty(m)));
                }
            }
        }
        catch (Exception ex)
        {
            LogWarning("获取ModalitiesInStudy失败: {Error}", ex.Message);
        }

        // 如果没有ModalitiesInStudy，则使用Series级别的Modality
        var modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
        if (!string.IsNullOrEmpty(modality))
        {
            return modality;
        }

        return string.Empty;
    }

    private void HandleFailedBatch(List<(DicomDataset Dataset, string FilePath)> failedItems)
    {
        // 可以实现失败理逻辑，比如：
        // 1. 写入错误日志文件
        // 2. 存入特定的误表
        // 3. 送告警通知
        // 4. 放入重试队列等
    }

    public async Task SaveDicomDataAsync(DicomDataset dataset, string filePath)
    {
        _dataQueue.Enqueue((dataset, filePath));
        
        // 当队列达到批处理小的80%时，主动触发处理
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
        (string StartDate, string EndDate) dateRange,
        string modality,
        string scheduledStationName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT * FROM Worklist 
                WHERE 1=1
                AND (@PatientId = '' OR PatientId LIKE @PatientId)
                AND (@AccessionNumber = '' OR AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR @EndDate = '' OR 
                     substr(ScheduledDateTime, 1, 8) >= @StartDate AND 
                     substr(ScheduledDateTime, 1, 8) <= @EndDate)
                AND (@Modality = '' OR Modality = @Modality)
                AND (@ScheduledStationName = '' OR ScheduledStationName = @ScheduledStationName)
                AND Status = 'SCHEDULED'
                ORDER BY CreateTime DESC";

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                Modality = string.IsNullOrEmpty(modality) ? "" : modality,
                ScheduledStationName = string.IsNullOrEmpty(scheduledStationName) ? "" : scheduledStationName
            };

            LogDebug("执行工作列表查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            var items = connection.Query<WorklistItem>(sql, parameters);
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

    public List<Study> GetStudies(
        string patientId, 
        string patientName, 
        string accessionNumber, 
        (string StartDate, string EndDate) dateRange,
        string[]? modalities)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    s.*,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfStudyRelatedSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfStudyRelatedInstances
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                GROUP BY 
                    s.StudyInstanceUid,
                    s.PatientId,
                    s.StudyDate,
                    s.StudyTime,
                    s.StudyDescription,
                    s.AccessionNumber,
                    s.Modality,
                    s.CreateTime,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate
                ORDER BY s.CreateTime DESC";

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                ModCount = modalities?.Length ?? 0,
                Modalities = (modalities?.Length ?? 0) > 0 ? modalities : new[] { "" }
            };

            LogDebug("执行检查查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            var studies = connection.Query<Study>(sql, parameters);
            var result = studies?.ToList() ?? new List<Study>();
            var modalitiesStr = modalities != null ? string.Join(",", modalities) : string.Empty;
            LogInformation("检查查询完成 - 返回记录数: {Count}, 日期范围: {StartDate} - {EndDate}, 设备类型: {Modalities}", 
                result.Count, dateRange.StartDate, dateRange.EndDate, modalitiesStr);
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

    public void UpdateWorklistStatus(string scheduledStepId, string status)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Worklist 
            SET Status = @Status 
            WHERE ScheduledProcedureStepID = @StepID";

        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@StepID", scheduledStepId);

        command.ExecuteNonQuery();
    }

    #region PrintSCP Operations

    /// <summary>
    /// 添加新的打印任务
    /// </summary>
    public async Task<bool> AddPrintJobAsync(PrintJob job)
    {
        try
        {
            // 确保时间是地时间
            if (job.CreateTime == default)
            {
                job.CreateTime = DateTime.Now;
            }
            if (job.UpdateTime == default)
            {
                job.UpdateTime = DateTime.Now;
            }

            using var connection = CreateConnection();
            var sql = @"
                INSERT INTO PrintJobs (
                    JobId, FilmSessionId, FilmBoxId, CallingAE,
                    Status, ImagePath, PatientId, PatientName, 
                    AccessionNumber, FilmSize, FilmOrientation, FilmLayout,
                    MagnificationType, BorderDensity, EmptyImageDensity,
                    MinDensity, MaxDensity, TrimValue, ConfigurationInfo,
                    CreateTime, UpdateTime
                ) VALUES (
                    @JobId, @FilmSessionId, @FilmBoxId, @CallingAE,
                    @Status, @ImagePath, @PatientId, @PatientName,
                    @AccessionNumber, @FilmSize, @FilmOrientation, @FilmLayout,
                    @MagnificationType, @BorderDensity, @EmptyImageDensity,
                    @MinDensity, @MaxDensity, @TrimValue, @ConfigurationInfo,
                    @CreateTime, @UpdateTime
                )";

            var result = await connection.ExecuteAsync(sql, job);
            LogDebug("添加打印任务 - JobId: {JobId}, 结果: {Result}", job.JobId, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "添加打印任务失败 - JobId: {JobId}", job.JobId);
            throw;
        }
    }

    /// <summary>
    /// 更新打印任务状态和图像路径
    /// </summary>
    public async Task<bool> UpdatePrintJobStatusAsync(string jobId, string status, string? imagePath = null)
    {
        try
        {
            using var connection = CreateConnection();
            var updates = new List<string> { "Status = @Status", "UpdateTime = @UpdateTime" };
            var parameters = new DynamicParameters();
            parameters.Add("@JobId", jobId);
            parameters.Add("@Status", status);
            parameters.Add("@UpdateTime", DateTime.Now.ToLocalTime());

            if (imagePath != null)
            {
                updates.Add("ImagePath = @ImagePath");
                parameters.Add("@ImagePath", imagePath);
            }

            var sql = $"UPDATE PrintJobs SET {string.Join(", ", updates)} WHERE JobId = @JobId";
            var result = await connection.ExecuteAsync(sql, parameters);
            LogDebug("更新打印任务状态 - JobId: {JobId}, 状态: {Status}, 结果: {Result}", jobId, status, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新打印任务状态失败 - JobId: {JobId}, 状态: {Status}", jobId, status);
            throw;
        }
    }

    /// <summary>
    /// 更新打印任务信息
    /// </summary>
    public async Task<bool> UpdatePrintJobAsync(
        string filmSessionId, 
        string? filmBoxId = null, 
        Dictionary<string, string>? printParams = null,
        Dictionary<string, string>? patientInfo = null)
    {
        try
        {
            using var connection = CreateConnection();
            var updates = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("@FilmSessionId", filmSessionId);
            parameters.Add("@UpdateTime", DateTime.Now.ToLocalTime());

            if (filmBoxId != null)
            {
                updates.Add("FilmBoxId = @FilmBoxId");
                parameters.Add("@FilmBoxId", filmBoxId);
            }

            if (printParams != null)
            {
                if (printParams.TryGetValue("FilmSize", out var filmSize))
                {
                    updates.Add("FilmSize = @FilmSize");
                    parameters.Add("@FilmSize", filmSize);
                }
                if (printParams.TryGetValue("FilmOrientation", out var filmOrientation))
                {
                    updates.Add("FilmOrientation = @FilmOrientation");
                    parameters.Add("@FilmOrientation", filmOrientation);
                }
                if (printParams.TryGetValue("FilmLayout", out var filmLayout))
                {
                    updates.Add("FilmLayout = @FilmLayout");
                    parameters.Add("@FilmLayout", filmLayout);
                }
                if (printParams.TryGetValue("MagnificationType", out var magnificationType))
                {
                    updates.Add("MagnificationType = @MagnificationType");
                    parameters.Add("@MagnificationType", magnificationType);
                }
                if (printParams.TryGetValue("BorderDensity", out var borderDensity))
                {
                    updates.Add("BorderDensity = @BorderDensity");
                    parameters.Add("@BorderDensity", borderDensity);
                }
                if (printParams.TryGetValue("EmptyImageDensity", out var emptyImageDensity))
                {
                    updates.Add("EmptyImageDensity = @EmptyImageDensity");
                    parameters.Add("@EmptyImageDensity", emptyImageDensity);
                }
                if (printParams.TryGetValue("MinDensity", out var minDensity))
                {
                    updates.Add("MinDensity = @MinDensity");
                    parameters.Add("@MinDensity", minDensity);
                }
                if (printParams.TryGetValue("MaxDensity", out var maxDensity))
                {
                    updates.Add("MaxDensity = @MaxDensity");
                    parameters.Add("@MaxDensity", maxDensity);
                }
                if (printParams.TryGetValue("TrimValue", out var trimValue))
                {
                    updates.Add("TrimValue = @TrimValue");
                    parameters.Add("@TrimValue", trimValue);
                }
                if (printParams.TryGetValue("ConfigurationInfo", out var configurationInfo))
                {
                    updates.Add("ConfigurationInfo = @ConfigurationInfo");
                    parameters.Add("@ConfigurationInfo", configurationInfo);
                }
            }

            if (patientInfo != null)
            {
                if (patientInfo.TryGetValue("PatientId", out var patientId))
                {
                    updates.Add("PatientId = @PatientId");
                    parameters.Add("@PatientId", patientId);
                }
                if (patientInfo.TryGetValue("PatientName", out var patientName))
                {
                    updates.Add("PatientName = @PatientName");
                    parameters.Add("@PatientName", patientName);
                }
                if (patientInfo.TryGetValue("AccessionNumber", out var accessionNumber))
                {
                    updates.Add("AccessionNumber = @AccessionNumber");
                    parameters.Add("@AccessionNumber", accessionNumber);
                }
            }

            updates.Add("UpdateTime = @UpdateTime");

            if (updates.Count == 0)
            {
                return true;
            }

            var sql = $"UPDATE PrintJobs SET {string.Join(", ", updates)} WHERE FilmSessionId = @FilmSessionId";
            var result = await connection.ExecuteAsync(sql, parameters);
            LogDebug("更新打印任务 - FilmSessionId: {FilmSessionId}, 结果: {Result}", filmSessionId, result > 0);
            return result > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新打印任务失败 - FilmSessionId: {FilmSessionId}", filmSessionId);
            throw;
        }
    }

    /// <summary>
    /// 根据FilmBoxId获取打印任务
    /// </summary>
    public async Task<PrintJob?> GetPrintJobByFilmBoxIdAsync(string filmBoxId)
    {
        try
        {
            if (string.IsNullOrEmpty(filmBoxId))
                return null;

            using var connection = CreateConnection();
            var sql = @"
                SELECT JobId, FilmSessionId, FilmBoxId, ImageBoxId, CallingAE,
                       Status, ImagePath, PrinterName, ErrorMessage,
                       PatientId, PatientName, AccessionNumber,
                       FilmSize, FilmOrientation, FilmLayout, MagnificationType,
                       BorderDensity, EmptyImageDensity, MinDensity, MaxDensity,
                       TrimValue, ConfigurationInfo, CreateTime, UpdateTime
                FROM PrintJobs
                WHERE FilmBoxId = @FilmBoxId";

            var job = await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { FilmBoxId = filmBoxId });
            LogDebug("获取打印任务 - FilmBoxId: {FilmBoxId}, 找到: {Found}", filmBoxId, job != null);
            return job;
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务失败 - FilmBoxId: {FilmBoxId}", filmBoxId);
            throw;
        }
    }

    /// <summary>
    /// 获取指定状态的打印任务列表
    /// </summary>
    public async Task<IEnumerable<PrintJob>> GetPrintJobsByStatusAsync(string status)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT JobId, FilmSessionId, FilmBoxId, ImageBoxId, CallingAE,
                       Status, ImagePath, PrinterName, ErrorMessage,
                       PatientId, PatientName, AccessionNumber,
                       FilmSize, FilmOrientation, FilmLayout, MagnificationType,
                       BorderDensity, EmptyImageDensity, MinDensity, MaxDensity,
                       TrimValue, ConfigurationInfo, CreateTime, UpdateTime
                FROM PrintJobs
                WHERE Status = @Status
                ORDER BY CreateTime DESC";

            var jobs = await connection.QueryAsync<PrintJob>(sql, new { Status = status });
            LogDebug("获取打印任务列表 - Status: {Status}, 数量: {Count}", status, jobs.Count());
            return jobs;
        }
        catch (Exception ex)
        {
            LogError(ex, "获取打印任务列表失败 - Status: {Status}", status);
            throw;
        }
    }

    /// <summary>
    /// 获取最近的打印任务
    /// </summary>
    public async Task<PrintJob?> GetMostRecentPrintJobAsync()
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT TOP 1 JobId, FilmSessionId, FilmBoxId, ImageBoxId, CallingAE,
                       Status, ImagePath, PrinterName, ErrorMessage,
                       PatientId, PatientName, AccessionNumber,
                       FilmSize, FilmOrientation, FilmLayout, MagnificationType,
                       BorderDensity, EmptyImageDensity, MinDensity, MaxDensity,
                       TrimValue, ConfigurationInfo, CreateTime, UpdateTime
                FROM PrintJobs
                WHERE FilmBoxId IS NOT NULL
                ORDER BY CreateTime DESC";

            var job = await connection.QueryFirstOrDefaultAsync<PrintJob>(sql);
            LogDebug("获取最近打印任务 - 找到: {Found}", job != null);
            return job;
        }
        catch (Exception ex)
        {
            LogError(ex, "获取最近打印任务失败");
            throw;
        }
    }

    #endregion

    #region Print Management API

    /// <summary>
    /// 获取所有打印任务列表
    /// </summary>
    public async Task<List<PrintJob>> GetPrintJobsAsync(string? status = null)
    {
        using var connection = CreateConnection();
        var sql = "SELECT * FROM PrintJobs";
        if (!string.IsNullOrEmpty(status))
        {
            sql += " WHERE Status = @Status";
            var jobs = await connection.QueryAsync<PrintJob>(sql, new { Status = status });
            return jobs.ToList();
        }
        var allJobs = await connection.QueryAsync<PrintJob>(sql);
        return allJobs.ToList();
    }

    /// <summary>
    /// 获取单个打印任务
    /// </summary>
    public async Task<PrintJob?> GetPrintJobAsync(string jobId)
    {
        using var connection = CreateConnection();
        var sql = "SELECT * FROM PrintJobs WHERE JobId = @JobId";
        return await connection.QueryFirstOrDefaultAsync<PrintJob>(sql, new { JobId = jobId });
    }

    /// <summary>
    /// 删除打印任务
    /// </summary>
    public async Task<bool> DeletePrintJobAsync(string jobId)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "DELETE FROM PrintJobs WHERE JobId = @JobId";
            var rowsAffected = await connection.ExecuteAsync(sql, new { JobId = jobId });
            LogDebug("删除打印任务 - JobId: {JobId}, 成功: {Success}", jobId, rowsAffected > 0);
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "删除打印任务失败 - JobId: {JobId}", jobId);
            throw;
        }
    }

    #endregion

    public IEnumerable<Patient> GetPatients(string patientId, string patientName)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    p.PatientId, 
                    p.PatientName, 
                    p.PatientBirthDate, 
                    p.PatientSex,
                    p.CreateTime,
                    COUNT(DISTINCT s.StudyInstanceUid) as NumberOfStudies,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfInstances
                FROM Patients p
                LEFT JOIN Studies s ON p.PatientId = s.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1";

            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(patientId))
            {
                sql += " AND p.PatientId LIKE @PatientId";
                parameters.Add("@PatientId", $"%{patientId}%");
            }

            if (!string.IsNullOrEmpty(patientName))
            {
                sql += " AND p.PatientName LIKE @PatientName";
                parameters.Add("@PatientName", $"%{patientName}%");
            }

            sql += @" 
                GROUP BY p.PatientId, p.PatientName, p.PatientBirthDate, p.PatientSex, p.CreateTime
                ORDER BY p.PatientName";

            LogDebug("执行Patient查询 - SQL: {Sql}, PatientId: {PatientId}, PatientName: {PatientName}", 
                sql, patientId, patientName);

            var patients = connection.Query<Patient>(sql, parameters).ToList();

            LogInformation("Patient查询完成 - 返回记录数: {Count}", patients.Count);

            return patients;
        }
        catch (Exception ex)
        {
            LogError(ex, "Patient查询失败 - PatientId: {PatientId}, PatientName: {PatientName}", 
                patientId, patientName);
            return Enumerable.Empty<Patient>();
        }
    }

    public async Task UpdateStudyModalityAsync(string studyInstanceUid, string modality)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                UPDATE Studies 
                SET Modality = @Modality 
                WHERE StudyInstanceUid = @StudyInstanceUid 
                AND (Modality IS NULL OR Modality = '')";

            await connection.ExecuteAsync(sql, new { 
                StudyInstanceUid = studyInstanceUid, 
                Modality = modality 
            });

            LogDebug("更新Study Modality - StudyInstanceUID: {StudyInstanceUid}, Modality: {Modality}", 
                studyInstanceUid, modality);
        }
        catch (Exception ex)
        {
            LogError(ex, "更新Study Modality失败 - StudyInstanceUID: {StudyInstanceUid}, Modality: {Modality}", 
                studyInstanceUid, modality);
        }
    }
}