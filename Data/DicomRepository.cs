using Microsoft.Data.Sqlite;
using Dapper;
using DicomSCP.Models;
using FellowOakDicom;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Configuration;

namespace DicomSCP.Data;

public class DicomRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DicomRepository> _logger;
    private readonly ConcurrentQueue<(DicomDataset Dataset, string FilePath)> _dataQueue = new();
    private readonly int _batchSize = 50;
    private readonly SemaphoreSlim _processSemaphore = new(1, 1);
    private readonly Timer _processTimer;

    public DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DicomDb") 
            ?? "Data Source=dicom.db";
        _logger = logger;

        InitializeDatabase();

        // 创建定时器，每5秒检查一次队列
        _processTimer = new Timer(async _ => 
        {
            if (_dataQueue.Count > 0)
            {
                await ProcessBatchAsync();
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Patients (
                PatientId TEXT PRIMARY KEY,
                PatientName TEXT,
                PatientBirthDate TEXT,
                PatientSex TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS Studies (
                StudyInstanceUid TEXT PRIMARY KEY,
                PatientId TEXT,
                StudyDate TEXT,
                StudyTime TEXT,
                StudyDescription TEXT,
                AccessionNumber TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY(PatientId) REFERENCES Patients(PatientId)
            );

            CREATE TABLE IF NOT EXISTS Series (
                SeriesInstanceUid TEXT PRIMARY KEY,
                StudyInstanceUid TEXT,
                Modality TEXT,
                SeriesNumber TEXT,
                SeriesDescription TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY(StudyInstanceUid) REFERENCES Studies(StudyInstanceUid)
            );

            CREATE TABLE IF NOT EXISTS Instances (
                SopInstanceUid TEXT PRIMARY KEY,
                SeriesInstanceUid TEXT,
                SopClassUid TEXT,
                InstanceNumber TEXT,
                FilePath TEXT,
                CreateTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY(SeriesInstanceUid) REFERENCES Series(SeriesInstanceUid)
            );
        ");
    }

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
    }

    public async Task SaveDicomDataAsync(DicomDataset dataset, string filePath)
    {
        _dataQueue.Enqueue((dataset, filePath));
        
        // 达到批量大小时立即处理
        if (_dataQueue.Count >= _batchSize)
        {
            await ProcessBatchAsync();
        }
    }

    private async Task ProcessBatchAsync()
    {
        await _processSemaphore.WaitAsync();
        try
        {
            if (_dataQueue.IsEmpty) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var batchData = new List<(DicomDataset Dataset, string FilePath)>();
                while (_dataQueue.TryDequeue(out var item) && batchData.Count < _batchSize)
                {
                    batchData.Add(item);
                }

                if (batchData.Count > 0)
                {
                    _logger.LogInformation("开始批量保存 - 数量: {Count}", batchData.Count);

                    var now = DateTime.UtcNow;
                    var patients = new List<Patient>();
                    var studies = new List<Study>();
                    var series = new List<Series>();
                    var instances = new List<Instance>();

                    foreach (var (dataset, filePath) in batchData)
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

                    await connection.ExecuteAsync(SqlQueries.InsertPatient, patients, transaction);
                    await connection.ExecuteAsync(SqlQueries.InsertStudy, studies, transaction);
                    await connection.ExecuteAsync(SqlQueries.InsertSeries, series, transaction);
                    await connection.ExecuteAsync(SqlQueries.InsertInstance, instances, transaction);

                    await transaction.CommitAsync();
                    _logger.LogInformation("批量保存完成 - 总数: {Count}", instances.Count);
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "批量保存失败");
                throw;
            }
        }
        finally
        {
            _processSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _processTimer?.Dispose();
        _processSemaphore?.Dispose();
    }
}