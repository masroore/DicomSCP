# DICOM SCP 服务器

基于 .NET Core 的 DICOM SCP（Service Class Provider）服务器，提供 DICOM 存储、工作列表、查询检索和打印服务。

## 功能特性

### DICOM 服务
- **存储服务 (C-STORE SCP)**
  - 支持多种传输语法和压缩格式
  - 可配置并发存储限制
  - 自动组织存储目录结构
  - 支持 JPEG、JPEG2000、JPEG-LS 压缩

- **工作列表服务 (Worklist SCP)**
  - 提供标准 DICOM Modality Worklist 服务
  - 支持多种查询条件（患者ID、检查号、日期等）
  - 实时更新工作列表数据
  - 支持预约状态管理

- **查询检索服务 (QR SCP)**
  - 支持 Study Root 级别查询
  - 提供 C-FIND、C-MOVE、C-GET 服务
  - 可配置多个目标节点
  - 支持多种查询级别（Study/Series/Image）

- **打印服务 (Print SCP)**
  - 支持基本灰阶和彩色打印
  - 打印任务队列管理
  - 支持多种打印格式
  - 打印任务状态跟踪

### Web 管理界面
- **服务管理**
  - 实时服务状态监控
  - AE Title 和端口配置
  - 服务参数调整
  - 日志实时查看

- **数据管理**
  - 影像数据浏览
  - 工作列表管理
  - 打印任务管理
  - 系统配置管理

### 数据存储
- **SQLite 数据库**
  - 存储影像元数据
  - 管理工作列表数据
  - 记录打印任务
  - 自动维护数据一致性

### 日志系统
- **分模块日志**
  - 按服务类型分类
  - 支持多种日志级别
  - 日志文件自动轮转
  - 可配置保留天数

## 系统要求

- Windows 10/11 或 Windows Server 2016+
- .NET 7.0 或更高版本
- SQLite 3.x
- 2GB+ RAM
- 1GB+ 可用磁盘空间

## 快速开始

1. 下载最新发布版本
2. 修改 appsettings.json 配置文件
3. 运行 DicomSCP.exe
4. 访问 http://localhost:5000

## 配置说明

主要配置项（appsettings.json）：
```json
{
  "DicomSettings": {
    "AeTitle": "STORESCP",
    "StoreSCPPort": 11112,
    "StoragePath": "D:\\dicom\\storage",
    "TempPath": "./temp",
    "WorklistSCP": {
      "AeTitle": "WORKLISTSCP",
      "Port": 11113
    },
    "QRSCP": {
      "AeTitle": "QRSCP",
      "Port": 11114
    },
    "PrintSCP": {
      "AeTitle": "PRINTSCP",
      "Port": 11115
    }
  }
}
```

## 编译说明

发布单文件可执行程序：
```bash
dotnet publish -r win-x64 -c Release /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --self-contained true
```

## 常见问题

1. **端口占用**
   - 确保配置的端口未被其他程序占用
   - 检查防火墙设置

2. **存储路径**
   - 确保配置的存储路径存在且有写入权限
   - 建议使用绝对路径

3. **服务连接**
   - 验证 AE Title 配置
   - 检查网络连接和防火墙设置
   - 确认远程设备配置正确

## 技术支持

- 提交 Issue
- 发送邮件至 support@example.com
- 查看在线文档

## 许可证

MIT License
