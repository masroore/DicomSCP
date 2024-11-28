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

在appsettings.json中配置：

```json
{
  "DicomSettings": {
    "AeTitle": "STORESCP",    // DICOM AE标题
    "Port": 11112,            // 监听端口
    "StoragePath": "./received_files",  // 文件存储路径
    "MaxPDULength": 16384,    // 最大PDU长度
    "ValidateCallingAE": false,  // 是否验证调用方AE
    "AllowedCallingAEs": []   // 允许的调用方AE列表
  }
}
```

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
