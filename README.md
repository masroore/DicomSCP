# DicomSCP

这是一个基于.NET Core 8和fo-dicom库实现的DICOM SCP(Service Class Provider)服务器。

## 功能特性

- 支持C-STORE SCP服务
- 接收和存储DICOM图像
- 可配置的存储路径
- 支持多种传输语法
- 日志记录功能

## 使用方法

1. 配置设置
   - AE Title: 默认为"STORESCU"
   - 端口: 默认为11112
   - 存储路径: 默认为"./received_files"

2. 运行服务
   ```bash
   dotnet run
   ```

3. 服务启动后，将在控制台显示监听状态，等待DICOM客户端连接。

## 配置说明

在appsettings.json中可以修改以下配置：
