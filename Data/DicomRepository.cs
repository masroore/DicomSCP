using Microsoft.Data.Sqlite;
using Dapper;
using DicomSCP.Models;
using FellowOakDicom;
using Microsoft.Extensions.Logging;
using System.IO;

namespace DicomSCP.Data;

public class DicomRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DicomRepository> _logger;

    public DicomRepository(IConfiguration configuration, ILogger<DicomRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DicomDb") 
            ?? "Data Source=dicom.db";
        _logger = logger;

        var dbPath = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var dbDirectory = Path.GetDirectoryName(dbPath);
        
        _logger.LogInformation("数据库路径: {DbPath}", dbPath);
        
        if (!string.IsNullOrEmpty(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
            _logger.LogInformation("确保数据库目录存在: {DbDirectory}", dbDirectory);
        }

        InitializeDatabase();
        _logger.LogInformation("数据库初始化完成");
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

    public async Task SaveDicomDataAsync(DicomDataset dataset, string filePath)
    {
        using var connection = new SqliteConnection(_connectionString);
        
        try
        {
            // 先打开连接
            await connection.OpenAsync();
            
            // 然后开始事务
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // 保存Patient信息
                var patient = new Patient
                {
                    PatientId = dataset.GetSingleValue<string>(DicomTag.PatientID),
                    PatientName = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty),
                    PatientBirthDate = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientBirthDate, string.Empty),
                    PatientSex = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientSex, string.Empty),
                    CreateTime = DateTime.UtcNow
                };

                _logger.LogInformation("正在保存Patient信息 - PatientId: {PatientId}", patient.PatientId);

                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO Patients 
                    (PatientId, PatientName, PatientBirthDate, PatientSex, CreateTime)
                    VALUES (@PatientId, @PatientName, @PatientBirthDate, @PatientSex, @CreateTime)",
                    patient, transaction);

                // 保存Study信息
                var study = new Study
                {
                    StudyInstanceUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
                    PatientId = patient.PatientId,
                    StudyDate = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty),
                    StudyTime = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty),
                    StudyDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDescription, string.Empty),
                    AccessionNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
                    CreateTime = DateTime.UtcNow
                };

                _logger.LogInformation("正在保存Study信息 - StudyInstanceUid: {StudyInstanceUid}", study.StudyInstanceUid);

                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO Studies 
                    (StudyInstanceUid, PatientId, StudyDate, StudyTime, StudyDescription, AccessionNumber, CreateTime)
                    VALUES (@StudyInstanceUid, @PatientId, @StudyDate, @StudyTime, @StudyDescription, @AccessionNumber, @CreateTime)",
                    study, transaction);

                // 保存Series信息
                var series = new Series
                {
                    SeriesInstanceUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID),
                    StudyInstanceUid = study.StudyInstanceUid,
                    Modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty),
                    SeriesNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesNumber, string.Empty),
                    SeriesDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesDescription, string.Empty),
                    CreateTime = DateTime.UtcNow
                };

                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO Series 
                    (SeriesInstanceUid, StudyInstanceUid, Modality, SeriesNumber, SeriesDescription, CreateTime)
                    VALUES (@SeriesInstanceUid, @StudyInstanceUid, @Modality, @SeriesNumber, @SeriesDescription, @CreateTime)",
                    series, transaction);

                // 保存Instance信息
                var instance = new Instance
                {
                    SopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
                    SeriesInstanceUid = series.SeriesInstanceUid,
                    SopClassUid = dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
                    InstanceNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.InstanceNumber, string.Empty),
                    FilePath = filePath,
                    CreateTime = DateTime.UtcNow
                };

                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO Instances 
                    (SopInstanceUid, SeriesInstanceUid, SopClassUid, InstanceNumber, FilePath, CreateTime)
                    VALUES (@SopInstanceUid, @SeriesInstanceUid, @SopClassUid, @InstanceNumber, @FilePath, @CreateTime)",
                    instance, transaction);

                await transaction.CommitAsync();
                _logger.LogInformation("已成功保存DICOM数据到数据库 - SOPInstanceUID: {SopInstanceUid}", instance.SopInstanceUid);
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "执行数据库事务失败 - 错误详情: {ErrorMessage}", innerEx.Message);
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存DICOM数据到数据库失败 - 错误详情: {ErrorMessage}", ex.Message);
            if (ex.InnerException != null)
            {
                _logger.LogError("内部错误: {InnerError}", ex.InnerException.Message);
            }
            throw;
        }
    }
}