# DICOM SCP 服务器

基于 .NET Core 的 DICOM SCP（Service Class Provider）服务器，提供 DICOM 存储、工作列表和查询检索服务。

## 功能特性

### DICOM 服务
- **存储服务 (C-STORE SCP)**
  - 支持多种传输语法
  - 可配置并发存储限制
  - 支持图像压缩
  - 自动组织存储目录结构

- **工作列表服务 (Worklist SCP)**
  - 提供标准 DICOM 工作列表查询
  - 支持多种查询条件
  - 实时更新工作列表数据

- **查询检索服务 (Query/Retrieve SCU)**
  - 支持远程节点配置
  - 提供 C-FIND 和 C-MOVE 功能
  - 可配置多个远程 PACS 节点

### Web API
- **RESTful API**
  - 服务状态管理（启动/停止）
  - 工作列表管理
  - 影像数据管理
  - 用户认证和授权

### 数据管理
- **SQLite 数据库**
  - 存储影像元数据
  - 管理工作列表数据
  - 用户账户管理

### 日志系统
- **分模块日志**
  - DICOM 服务日志
  - API 访问日志
  - 数据库操作日志
  - 可配置日志级别和输出方式

## 配置说明

### 主要配置项
```json
{
  "DicomSettings": {
    "AeTitle": "STORESCP",
    "StoreSCPPort": 11112,
    "StoragePath": "./received_files",
    "TempPath": "./temp_files",
    "Advanced": {
      "ValidateCallingAE": false,
      "AllowedCallingAEs": [],
      "ConcurrentStoreLimit": 0,
      "EnableCompression": false
    }
  },
  "WorklistSCP": {
    "AeTitle": "WORKLISTSCP",
    "Port": 11113,
    "ValidateCallingAE": false,
    "AllowedCallingAEs": []
  },
  "QueryRetrieveConfig": {
    "LocalAeTitle": "QUERYSCU",
    "LocalPort": 11114,
    "RemoteNodes": []
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  }
}
```

### 日志配置
```json
{
  "Logging": {
    "LogPath": "logs",
    "RetainedDays": 31,
    "Services": {
      "StoreSCP": {
        "Enabled": true,
        "MinimumLevel": "Information",
        "EnableConsoleLog": true,
        "EnableFileLog": true
      }
    }
  }
}
```

## 项目结构

### 配置模块 (Configuration/)
- `DicomSettings.cs`: DICOM 服务基础配置
- `WorklistSCPSettings.cs`: 工作列表服务配置
- `LogSettings.cs`: 日志系统配置

### 控制器 (Controllers/)
- `DicomController.cs`: DICOM 服务控制
- `WorklistController.cs`: 工作列表管理
- `ImagesController.cs`: 影像数据管理
- `AuthController.cs`: 用户认证

### 数据访问 (Data/)
- `DicomRepository.cs`: DICOM 数据访问
- `WorklistRepository.cs`: 工作列表数据访问
- `BaseRepository.cs`: 数据访问基类

### 服务层 (Services/)
- `DicomServer.cs`: DICOM 服务管理
- `WorklistSCP.cs`: 工作列表服务实现
- `DicomLogger.cs`: 日志服务
- `ApiLoggingMiddleware.cs`: API 日志中间件

## 使用说明

### 安装依赖
```bash
dotnet restore
```

### 运行服务
```bash
dotnet run
```

### 访问 API
- Web API: http://localhost:5000
- Swagger 文档: http://localhost:5000/swagger

### 默认账户
- 用户名: admin
- 密码: admin

## 常见问题

### 1. 远程访问问题
如果只能通过 127.0.0.1:5000 访问，但无法通过 IP 地址访问，请检查：
- 确保 appsettings.json 中 Kestrel 配置正确：
  ```json
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  }
  ```
- 检查防火墙是否允许 5000 端口的入站连接
- 如果使用 Windows，确保在防火墙中添加应用程序例外
- 在 Linux 系统中，可能需要执行：
  ```bash
  sudo ufw allow 5000/tcp
  ```

### 2. 数据库访问问题
- 确保数据库文件有正确的读写权限
- 检查连接字符串配置是否正确
- 确保数据库路径存在且可访问

### 3. DICOM 服务连接问题
- 确保防火墙允许 DICOM 端口（11112-11114）的入站连接
- 验证 AE Title 配置是否正确
- 检查远程设备的 AE Title 是否在允许列表中（如果启用了验证）

## 注意事项
1. 首次运行会自动创建数据库和必要的表
2. 确保配置文件中的存储路径具有写入权限
3. 建议在生产环境中修改默认密码
4. 日志文件会按日期自动滚动
5. 支持 Windows 和 Linux 系统部署
6. 在生产环境中部署时，建议配置反向代理（如 Nginx）并启用 HTTPS
