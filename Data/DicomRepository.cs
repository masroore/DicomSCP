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
    private readonly int _maxRetryAttempts = 3;
    private readonly TimeSpan _maxWaitTime = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _minWaitTime = TimeSpan.FromSeconds(5);
    private readonly Stopwatch _performanceTimer = new();
    private DateTime _lastProcessTime = DateTime.UtcNow;
    private long _totalProcessed;
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

        var waitTime = DateTime.UtcNow - _lastProcessTime;
        var queueSize = _dataQueue.Count;

        // 判断是否需要处理
        if (queueSize < _batchSize && 
            waitTime < _maxWaitTime && 
            queueSize < 10)
        {
            return;
        }

        await ProcessBatchWithRetryAsync();
    }

    private async Task ProcessBatchWithRetryAsync()
    {
        if (!await _processSemaphore.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            return; // 如果无法获取信号量，放弃本次处理
        }

        try
        {
            _performanceTimer.Restart();
            var batchItems = new List<(DicomDataset Dataset, string FilePath)>();
            var batchSize = Math.Min(_dataQueue.Count, _batchSize);

            // 收集批处理数据
            while (batchItems.Count < batchSize && _dataQueue.TryDequeue(out var item))
            {
                batchItems.Add(item);
            }

            if (batchItems.Count == 0) return;

            var attempt = 0;
            var success = false;

            while (!success && attempt < _maxRetryAttempts)
            {
                try
                {
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

                        foreach (var (dataset, filePath) in batchItems)
                        {
                            // 提取数据并添加到相应的列表中
                            ExtractDicomData(dataset, filePath, now, 
                                patients, studies, series, instances);
                        }

                        // 批量插入数据
                        await connection.ExecuteAsync(SqlQueries.InsertPatient, patients, transaction);
                        await connection.ExecuteAsync(SqlQueries.InsertStudy, studies, transaction);
                        await connection.ExecuteAsync(SqlQueries.InsertSeries, series, transaction);
                        await connection.ExecuteAsync(SqlQueries.InsertInstance, instances, transaction);

                        await transaction.CommitAsync();
                        success = true;

                        // 更新统计信息
                        _totalProcessed += batchItems.Count;
                        _lastProcessTime = DateTime.UtcNow;

                        // 记录性能指标
                        _performanceTimer.Stop();
                        _logger.LogInformation(
                            "批量处理完成 - 数量: {Count}, 耗时: {Time}ms, 总处理: {Total}, 队列剩余: {Remaining}", 
                            batchItems.Count, 
                            _performanceTimer.ElapsedMilliseconds,
                            _totalProcessed,
                            _dataQueue.Count);
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    attempt++;
                    if (attempt >= _maxRetryAttempts)
                    {
                        _logger.LogError(ex, 
                            "批量处理失败 - 重试次数: {Attempts}, 批次大小: {Size}", 
                            attempt, batchItems.Count);
                        // 考虑将失败的数据放入错误队列或死信队列
                        HandleFailedBatch(batchItems);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "批量处理重试 - 当前次数: {Attempt}/{MaxAttempts}", 
                            attempt, _maxRetryAttempts);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); // 指数退避
                    }
                }
            }
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
            // 创建表
            connection.Execute(SqlQueries.CreateWorklistTable);
            // ... 其他表的创建 ...

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
                    )", demoWorklist);

                _logger.LogInformation("已初始化 Worklist 演示数据");
            }

            transaction.Commit();
            _initialized = true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "初始化数据库失败");
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