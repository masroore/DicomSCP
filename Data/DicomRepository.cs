using Dapper;
using DicomSCP.Models;
using FellowOakDicom;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DicomSCP.Data;

public class DicomRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DicomRepository> _logger;
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
    }

    public DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DicomDb") 
            ?? throw new ArgumentException("Missing DicomDb connection string");
        _logger = logger;
        _batchSize = configuration.GetValue<int>("DicomSettings:BatchSize", 50);

        // 初始化数据库
        InitializeDatabase();

        // 创建定时器
        _processTimer = new Timer(async _ => await ProcessQueueAsync(), 
            null, 
            _minWaitTime, 
            _minWaitTime);
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

                _logger.LogInformation(
                    "批量处理完成 - 数量: {Count}, 耗时: {Time}ms, 队列剩余: {Remaining}, 平均耗时: {AvgTime:F2}ms/条", 
                    batchItems.Count,
                    _performanceTimer.ElapsedMilliseconds,
                    _dataQueue.Count,
                    _performanceTimer.ElapsedMilliseconds / (double)batchItems.Count);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "批量处理失败 - 批次大小: {Count}, 将重新入队", batchItems.Count);
                
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
            _logger.LogError(ex, "处理批次时发生异常");
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

        try
        {
            // 创建所有表
            connection.Execute(SqlQueries.CreatePatientsTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateStudiesTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateSeriesTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateInstancesTable, transaction: transaction);
            connection.Execute(SqlQueries.CreateWorklistTable, transaction: transaction);

            // 检查是否已经有数据
            var count = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Worklist");
            
            // 只在表为空时创建演示数据
            if (count == 0)
            {
                var demoWorklist = new WorklistItem
                {
                    WorklistId = Guid.NewGuid().ToString("N"),
                    AccessionNumber = "ACC20231129001",
                    PatientId = "P20231129001",
                    PatientName = "Test^Patient",
                    PatientBirthDate = "19800101",
                    PatientSex = "M",
                    StudyInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID,
                    StudyDescription = "Chest X-Ray",
                    Modality = "CR",
                    ScheduledAET = "STORESCP",
                    ScheduledDateTime = DateTime.Now.ToString("yyyyMMdd HHmmss"),
                    ScheduledStationName = "ROOM1",
                    ScheduledProcedureStepID = "SPS001",
                    ScheduledProcedureStepDescription = "Chest PA and Lateral",
                    RequestedProcedureID = "RP001",
                    RequestedProcedureDescription = "Chest X-Ray PA and Lateral",
                    ReferringPhysicianName = "Referring^Doctor",
                    Status = "SCHEDULED",
                    BodyPartExamined = "CHEST",
                    ReasonForRequest = "Routine checkup",
                    CreateTime = DateTime.Now,
                    UpdateTime = DateTime.Now
                };

                connection.Execute(@"
                    INSERT INTO Worklist (
                        WorklistId, AccessionNumber, PatientId, PatientName, PatientBirthDate,
                        PatientSex, StudyInstanceUid, StudyDescription, Modality, ScheduledAET,
                        ScheduledDateTime, ScheduledStationName, ScheduledProcedureStepID,
                        ScheduledProcedureStepDescription, RequestedProcedureID,
                        RequestedProcedureDescription, ReferringPhysicianName, Status,
                        BodyPartExamined, ReasonForRequest,
                        CreateTime, UpdateTime
                    ) VALUES (
                        @WorklistId, @AccessionNumber, @PatientId, @PatientName, @PatientBirthDate,
                        @PatientSex, @StudyInstanceUid, @StudyDescription, @Modality, @ScheduledAET,
                        @ScheduledDateTime, @ScheduledStationName, @ScheduledProcedureStepID,
                        @ScheduledProcedureStepDescription, @RequestedProcedureID,
                        @RequestedProcedureDescription, @ReferringPhysicianName, @Status,
                        @BodyPartExamined, @ReasonForRequest,
                        @CreateTime, @UpdateTime
                    )", demoWorklist, transaction);

                _logger.LogInformation("已初始化 Worklist 演示数据");
            }

            transaction.Commit();
            _initialized = true;
            _logger.LogInformation("数据库表初始化完成");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "初始化数据库表失败");
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
}