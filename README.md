# DICOM SCP 服务

这是一个基于 fo-dicom 实现的 DICOM SCP（Service Class Provider）服务，支持 DICOM 图像的接收、存储和管理。

## 功能特性

### DICOM 服务
- 支持 C-STORE SCP 服务，用于接收 DICOM 图像
- 支持 C-ECHO SCP 服务，用于验证连接
- 支持多种传输语法，包括压缩格式
- 支持并发接收和处理
- 支持 AE Title 验证

### 图像存储
- 自动按照 Study/Series 层级组织存储目录
- 支持图像压缩（可配置）
- 防止重复存储
- 支持临时文件管理

### 数据库存储
- 使用 SQLite 数据库存储 DICOM 信息
- 支持高效的批量入库
- 支持自动重试机制
- 性能监控和统计

## 配置说明

在 appsettings.json 中可以配置以下选项：

### DICOM服务器配置
- `AeTitle`: DICOM服务器的AE标题，长度1-16字符，只能包含字母、数字、连字符和下划线
- `Port`: DICOM服务器监听端口，范围1-65535
- `StoragePath`: DICOM文件存储路径，支持相对或绝对路径
- `TempPath`: 临时文件存储路径，用于文件传输过程中的临时存储

### 日志配置
- `Logging.EnableConsoleLog`: 是否启用控制台日志（仅显示服务状态变化和错误）
- `Logging.EnableFileLog`: 是否启用文件日志
- `Logging.FileLogLevel`: 文件日志级别（Verbose|Debug|Information|Warning|Error|Fatal）
- `Logging.RetainedDays`: 日志文件保留天数
- `Logging.LogPath`: 日志文件存储路径

### Web服务器配置
- `Kestrel.Endpoints.Http.Url`: Web API监听地址

### DICOM高级配置
- `Advanced.ValidateCallingAE`: 是否验证调用方AE
- `Advanced.AllowedCallingAEs`: 允许的调用方AE列表
- `Advanced.ConcurrentStoreLimit`: 并发存储限制（0表示自动使用CPU核心数 * 2）
- `Advanced.EnableCompression`: 是否启用图像压缩存储
- `Advanced.PreferredTransferSyntax`: 首选压缩格式（JPEG2000Lossless|JPEGLSLossless|RLELossless|JPEG2000Lossy|JPEGProcess14）

### Swagger配置
- `Swagger.Enabled`: 是否启用Swagger
- `Swagger.Title`: API文档标题
- `Swagger.Version`: API版本
- `Swagger.Description`: API描述

## API接口

+ API文档可以通过Swagger UI访问：
+ ```
+ http://localhost:5000/swagger
+ ```
+ 
### 获取服务器状态
```
GET /api/dicom/status
```

### 启动服务器
```
POST /api/dicom/start
```

### 停止服务器
```
POST /api/dicom/stop
```

### 更新配置
```
POST /api/dicom/settings
```

## 运行说明

1. 安装.NET 8.0 SDK
2. 克隆仓库
3. 配置appsettings.json
4. 运行命令：
```bash
dotnet run
```

## 注意事项

- 确保存储路径有足够的磁盘空间
- 端口号需要未被占用
- 建议在防火墙中开放配置的端口

## 数据库结构

### Patients 表
- PatientId (TEXT, PK)
- PatientName (TEXT)
- PatientBirthDate (TEXT)
- PatientSex (TEXT)
- CreateTime (DATETIME)

### Studies 表
- StudyInstanceUid (TEXT, PK)
- PatientId (TEXT, FK)
- StudyDate (TEXT)
- StudyTime (TEXT)
- StudyDescription (TEXT)
- AccessionNumber (TEXT)
- CreateTime (DATETIME)

### Series 表
- SeriesInstanceUid (TEXT, PK)
- StudyInstanceUid (TEXT, FK)
- Modality (TEXT)
- SeriesNumber (TEXT)
- SeriesDescription (TEXT)
- CreateTime (DATETIME)

### Instances 表
- SopInstanceUid (TEXT, PK)
- SeriesInstanceUid (TEXT, FK)
- SopClassUid (TEXT)
- InstanceNumber (TEXT)
- FilePath (TEXT)
- CreateTime (DATETIME)

## 性能优化

### 批量处理机制
- 动态批量大小：根据队列大小自动调整
- 定时处理：避免小批量频繁处理
- 队列监控：实时监控处理性能

### 错误处理
- 自动重试：支持失败自动重试
- 指数退避：重试间隔递增
- 事务保护：确保数据一致性

## 使用建议

1. 根据实际需求调整批处理大小（BatchSize）
2. 监控数据库性能，适时调整处理策略
3. 定期维护数据库，清理不需要的数据
4. 确保存储路径有足够的磁盘空间
5. 建议开启图像压缩以节省存储空间

## 注意事项

1. 首次运行会自动创建数据库和相关表
2. 确保数据库文件所在目录有写入权限
3. 大量数据入库时注意监控内存使用
4. 建议定期备份数据库文件
