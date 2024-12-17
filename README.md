# DICOM 管理系统

## 携手CURSOR重磅发布，打造中文开源社区最完善的DICOM工具箱!
基于 .NET Core 的 DICOM SCP（Service Class Provider）服务器，提供 DICOM 存储、工作列表、查询检索服务，打印服务，并集成了 Web 端 DICOM 查看器。


![登录](about/登录.png)  
![影像管理](about/影像查看.png)  
![工作列表](about/worklistscp.png)  
![查询检索](about/qrscu.png) 
![发送图像](about/发送图像.png)  
![打印](about/打印管理.png)  
![配置](about/settings.png) 
![日志](about/logs.png) 
![监控](about/status.png) 


## 功能特性

### DICOM 服务
- **存储服务 (C-STORE SCP)**
  - 按照4个级别的标签入库和归档
  - 按照级别标签自动组织存储目录结构
  - 支持 JPEG、JPEG2000、JPEG-LS、RLE 等压缩
  - 对不标准的字符集中文字符进行乱码处理

- **工作列表服务 (Worklist SCP)**
  - 提供标准 DICOM Modality Worklist 服务
  - 支持多种查询条件（患者ID、检查号、日期等）
  - 支持请求字符集协商自动中英文转换

- **查询检索服务 (QR SCP)**
  - 提供 C-FIND、C-MOVE、C-GET 服务
  - 可配置多个目标节点
  - 支持多种查询级别（Study/Series/Image）
  - 支持JPEG、JPEG2000、JPEG-LS、RLE 传输语法实时转码

- **打印服务 (Print SCP)**
  - 打印任务队列管理
  - 支持多种打印格式
  - 打印任务状态跟踪
  - 归档打印的原始文件和标签

- **WADO 服务 (Web Access to DICOM Objects)**
- 必需参数
  - `requestType`: 必须为 "WADO"
  - `studyUID`: 研究实例 UID
  - `seriesUID`: 序列实例 UID
  - `objectUID`: 实例 UID

- 可选参数
  - `contentType`: 返回内容类型 不传默认 image/jpeg
    - `application/dicom`: 返回 DICOM 格式
    - `image/jpeg`: 返回 JPEG 格式
  
  - `transferSyntax`: DICOM 传输语法 UID 不传默认不转码
    - `1.2.840.10008.1.2`: Implicit VR Little Endian
    - `1.2.840.10008.1.2.1`: Explicit VR Little Endian
    - `1.2.840.10008.1.2.4.50`: JPEG Baseline
    - `1.2.840.10008.1.2.4.57`: JPEG Lossless
    - `1.2.840.10008.1.2.4.70`: JPEG Lossless SV1
    - `1.2.840.10008.1.2.4.90`: JPEG 2000 Lossless
    - `1.2.840.10008.1.2.4.91`: JPEG 2000 Lossy
    - `1.2.840.10008.1.2.4.80`: JPEG-LS Lossless
    - `1.2.840.10008.1.2.5`: RLE Lossless

  - `anonymize`: 是否匿名化
    - `yes`: 执行匿名化处理
    - 其他值或不传: 不进行匿名化

  - 完整请求参数例子
  ```
  http://localhost:5000/wado?requestType=WADO&studyUID=1.2.840.113704.1.111.5096.1719875982.1&seriesUID=1.3.46.670589.33.1.13252761201319485513.2557156297609063016&objectUID=1.3.46.670589.33.1.39304787935332940.2231985654917411587&contentType=application/dicom&transferSyntax=1.2.840.10008.1.2.4.70&anonymize=yes

  ```
- **CSTORE-SCU (CSTORE-SCU)**
  - 支持发送DICOM图像到DICOM SCP
  - 可配置多个目标节点

- **Print-SCU (Print-SCU)**
  - 支持将PRINTSCP接收到的图像打印到其他打印机或PRINTSCP服务
  - 构建打印图像会保留原始图像的标签信息

- **LOG SERVICE (日志服务)**
  - 支持查看、下载、删除日志
  - 个服务日志独立配置
  - 多日志级别配置
  - 服务预置详细日志 方便对接查找问题


## 系统要求

- Windows 10/11 或 Windows Server 2016+
- .NET 8.0 或更高版本
- SQLite 3.x
- 2GB+ RAM
- 1GB+ 可用磁盘空间
- 现代浏览器（Chrome/Firefox/Edge）

## 快速开始

1. 下载最新发布版本
2. 修改 appsettings.json 配置文件
3. 运行 DicomSCP.exe
4. 访问 http://localhost:5000  
5. 默认账号 admin / admin


## 技术栈

- 后端框架：.NET Core
- 前端框架：原生 JavaScript
- DICOM 处理：fo-dicom、Cornerstone.js
- 数据库：SQLite
- HTTP 客户端：Axios
- UI 组件：Bootstrap



## 许可证

MIT License
