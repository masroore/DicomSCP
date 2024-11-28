# DicomSCP

这是一个基于.NET Core 8和fo-dicom库实现的DICOM SCP(Service Class Provider)服务器。

## 功能特性

- 支持C-STORE SCP服务
- 支持C-ECHO服务
- 自动接收和存储DICOM图像
- 支持多种传输语法，包括压缩格式
- RESTful API控制接口
- 完整的日志记录

## 配置说明

在 appsettings.json 中可以配置以下选项：

### DICOM服务器配置
- `AeTitle`: DICOM服务器的AE标题，长度1-16字符，只能包含字母、数字、连字符和下划线
- `Port`: DICOM服务器监听端口，范围1-65535
- `StoragePath`: DICOM文件存储路径，支持相对或绝对路径

### 日志配置
- `Logging.EnableConsoleLog`: 是否启用控制台日志（仅显示服务状态变化和错误）
- `Logging.EnableFileLog`: 是否启用文件日志
- `Logging.FileLogLevel`: 文件日志级别（Verbose|Debug|Information|Warning|Error|Fatal）
- `Logging.RetainedDays`: 日志文件保留天数
- `Logging.LogPath`: 日志文件存储路径

### Web服务器配置
- `Kestrel.Endpoints.Http.Url`: Web API监听地址

## API接口

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
