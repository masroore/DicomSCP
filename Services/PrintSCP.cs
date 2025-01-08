using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;
using Microsoft.Extensions.Options;

namespace DicomSCP.Services;

public class PrintSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private readonly string _printPath;
    private readonly string _relativePrintPath = "prints";
    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;

    // 会话状态管理
    private class PrintSession
    {
        public DicomFilmSession? FilmSession { get; set; }
        public DicomFilmBox? CurrentFilmBox { get; set; }
        public string? CallingAE { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public Dictionary<int, DicomDataset> CachedImages { get; set; } = new Dictionary<int, DicomDataset>();
    }

    private PrintSession _session = new();

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly Dictionary<string, (int width, int height)> FilmSizes = new()
    {
        ["14INX17IN"] = (3480, 4230),  // 14x17英寸，按250 DPI计算
        ["11INX14IN"] = (2750, 3500),  // 11x14英寸
        ["8INX10IN"] = (2000, 2500),   // 8x10英寸
        ["A4"] = (2100, 2970),         // A4尺寸
        ["A3"] = (2970, 4200)          // A3尺寸
    };

    public PrintSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = DicomServiceProvider.GetRequiredService<IOptions<DicomSettings>>().Value;
        _repository = DicomServiceProvider.GetRequiredService<DicomRepository>();
        _printPath = Path.Combine(_settings.StoragePath, _relativePrintPath);
        Directory.CreateDirectory(_printPath);
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, 
                association.CallingAE);

            // AE Title 验证
            if (_settings.PrintSCP.ValidateCallingAE && 
                !_settings.PrintSCP.AllowedCallingAEs.Contains(association.CallingAE))
            {
                DicomLogger.Warning("PrintSCP", "拒绝未授权的 Calling AE: {CallingAE}", association.CallingAE);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CallingAENotRecognized);
            }

            if (_settings.PrintSCP.AeTitle != association.CalledAE)
            {
                DicomLogger.Warning("PrintSCP", "拒绝错误的 Called AE: {CalledAE}，期望：{ExpectedAE}", 
                    association.CalledAE, 
                    _settings.PrintSCP.AeTitle);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            _session.CallingAE = association.CallingAE;

            // 定义支持的服务类型
            var supportedSOPClasses = new DicomUID[]
            {
                DicomUID.BasicFilmSession,
                DicomUID.BasicFilmBox,
                DicomUID.BasicGrayscalePrintManagementMeta,
                DicomUID.BasicColorPrintManagementMeta,
                DicomUID.BasicGrayscaleImageBox,
                DicomUID.BasicColorImageBox,
                DicomUID.Verification  // C-ECHO
            };

            var hasValidPresentationContext = false;

            foreach (var pc in association.PresentationContexts)
            {
                DicomLogger.Information("PrintSCP", "处理表示上下文 - Abstract Syntax：{AbstractSyntax}", 
                    pc.AbstractSyntax.Name);

                if (!supportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    DicomLogger.Warning("PrintSCP", "不支持的服务类型：{AbstractSyntax}", 
                        pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    continue;
                }

                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                hasValidPresentationContext = true;
            }

            if (!hasValidPresentationContext)
            {
                DicomLogger.Warning("PrintSCP", "没有有效的表示上下文，拒绝关联请求");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.ApplicationContextNotSupported);
            }

            DicomLogger.Information("PrintSCP", "接受关联请求");
            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.ApplicationContextNotSupported);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("PrintSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("PrintSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        try
        {
            if (exception != null)
            {
                DicomLogger.Error("PrintSCP", exception, "连接异常关闭");
            }
            else
            {
                DicomLogger.Information("PrintSCP", "连接正常关闭");
            }

            CleanupSession();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "清理资源时发生错误");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                return await HandleFilmSessionCreateAsync(request);
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                return await HandleFilmBoxCreateAsync(request);
            }

            DicomLogger.Warning("PrintSCP", "不支持的SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
            return new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmSessionCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "创建 Film Session - Calling AE: {CallingAE}", _session.CallingAE ?? "Unknown");
            
            // 生成 FilmSessionId，不依赖 SOPInstanceUID
            var filmSessionId = DicomUID.Generate().UID;
            var printJob = new PrintJob
            {
                JobId = $"{DateTime.Now:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}",
                FilmSessionId = filmSessionId,  // 使用生成的ID
                CallingAE = _session.CallingAE ?? "",
                Status = PrintJobStatus.Created,
                
                // Film Session 参数
                NumberOfCopies = request.Dataset?.GetSingleValueOrDefault(DicomTag.NumberOfCopies, 1) ?? 1,
                PrintPriority = request.Dataset?.GetSingleValueOrDefault(DicomTag.PrintPriority, "LOW") ?? "LOW",
                MediumType = request.Dataset?.GetSingleValueOrDefault(DicomTag.MediumType, "BLUE FILM") ?? "BLUE FILM",
                FilmDestination = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmDestination, "MAGAZINE") ?? "MAGAZINE",
                
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };

            // 保存到数据库
            await _repository.AddPrintJobAsync(printJob);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            
            if (request.Dataset != null)
            {
                var responseDataset = new DicomDataset();
                foreach (var item in request.Dataset)
                {
                    responseDataset.Add(item);
                }
                responseDataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicFilmSession);
                responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, filmSessionId);
                response.Dataset = responseDataset;
            }

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.BasicFilmSession },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, filmSessionId }
            };

            SetCommandDataset(response, command);

            _session.FilmSession = new DicomFilmSession(request.Dataset)
            {
                SOPInstanceUID = filmSessionId
            };

            DicomLogger.Information("PrintSCP", "Film Session创建成功 - ID: {Id}", filmSessionId);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 Film Session 创建失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmBoxCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            if (_session.FilmSession == null)
            {
                DicomLogger.Warning("PrintSCP", "无法创建 Film Box - Film Session 不存在");
                return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            DicomLogger.Information("PrintSCP", "创建 Film Box - Film Session: {SessionId}, Calling AE: {CallingAE}", 
                _session.FilmSession.SOPInstanceUID,
                _session.CallingAE ?? "Unknown");

            // 生成 FilmBoxId
            var filmBoxId = DicomUID.Generate().UID;

            await _repository.UpdatePrintJobAsync(
                _session.FilmSession.SOPInstanceUID,
                filmBoxId: filmBoxId,
                parameters: new Dictionary<string, object>
                {
                    ["FilmOrientation"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT") ?? "PORTRAIT",
                    ["FilmSizeID"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmSizeID, "8INX10IN") ?? "8INX10IN",
                    ["ImageDisplayFormat"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1") ?? "STANDARD\\1,1",
                    ["MagnificationType"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.MagnificationType, "REPLICATE") ?? "REPLICATE",
                    ["SmoothingType"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.SmoothingType, "MEDIUM") ?? "MEDIUM",
                    ["BorderDensity"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.BorderDensity, "BLACK") ?? "BLACK",
                    ["EmptyImageDensity"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.EmptyImageDensity, "BLACK") ?? "BLACK",
                    ["Trim"] = request.Dataset?.GetSingleValueOrDefault(DicomTag.Trim, "NO") ?? "NO"
                });

            var responseDataset = new DicomDataset();
            
            // 复制原始请求中的属性
            if (request.Dataset != null)
            {
                foreach (var item in request.Dataset)
                {
                    if (item.Tag != DicomTag.ReferencedImageBoxSequence)
                    {
                        responseDataset.Add(item);
                    }
                }
            }

            responseDataset.AddOrUpdate(DicomTag.SOPClassUID, request.SOPClassUID);
            responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, filmBoxId);

            // 获取布局格式
            var imageDisplayFormat = request.Dataset?.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1") ?? "STANDARD\\1,1";
            var layoutParts = imageDisplayFormat.Split('\\')[1].Split(',');
            if (layoutParts.Length != 2 || !int.TryParse(layoutParts[0], out int columns) || !int.TryParse(layoutParts[1], out int rows))
            {
                DicomLogger.Warning("PrintSCP", "无效的布局格式: {Format}", imageDisplayFormat);
                return new DicomNCreateResponse(request, DicomStatus.InvalidAttributeValue);
            }

            DicomLogger.Information("PrintSCP", "布局: {Columns}列 x {Rows}行", columns, rows);

            // 根据布局创建Image Box引用
            var imageBoxSequence = new DicomSequence(DicomTag.ReferencedImageBoxSequence);
            var totalBoxes = columns * rows;

            for (int i = 1; i <= totalBoxes; i++)
            {
                var imageBoxDataset = new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomUID.BasicGrayscaleImageBox },
                    { DicomTag.ReferencedSOPInstanceUID, $"{filmBoxId}.{i}" },
                    { DicomTag.ImageBoxPosition, (ushort)i }
                };
                imageBoxSequence.Items.Add(imageBoxDataset);
                DicomLogger.Information("PrintSCP", "创建 Image Box {Position}/{Total}", i, totalBoxes);
            }

            responseDataset.Add(imageBoxSequence);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            response.Dataset = responseDataset;

            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8140 },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0102 },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, filmBoxId }  // 使用生成的filmBoxId
            };

            SetCommandDataset(response, command);

            _session.CurrentFilmBox = new DicomFilmBox(request.Dataset)
            {
                SOPInstanceUID = filmBoxId
            };

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 Film Box 创建失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _session.CallingAE ?? "Unknown");

            if (_session.FilmSession == null || _session.CurrentFilmBox == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session或Film Box为空，无法处理 N-SET 请求");
                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance));
            }

            // 获取图像序列
            var imageSequence = request.Dataset?.GetSequence(DicomTag.BasicGrayscaleImageSequence);
            if (imageSequence == null || !imageSequence.Items.Any())
            {
                DicomLogger.Warning("PrintSCP", "未找到图像序列或序列为空");
                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance));
            }

            // 从SOPInstanceUID中获取图像位置
            var sopInstanceUid = request.SOPInstanceUID?.UID ?? "";
            var position = 1;
            var parts = sopInstanceUid.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[parts.Length - 1], out int pos))
            {
                position = pos;
            }

            DicomLogger.Information("PrintSCP", "处理图像 - SOPInstanceUID: {Uid}, 位置: {Position}", sopInstanceUid, position);

            // 处理接收到的图像
            foreach (var item in imageSequence.Items)
            {
                if (item.Contains(DicomTag.PixelData))
                {
                    var pixelData = item.GetValues<byte>(DicomTag.PixelData);
                    DicomLogger.Information("PrintSCP", "图像 {Position} - 像素数据大小: {Size} bytes", position, pixelData?.Length ?? 0);
                    _session.CachedImages[position] = item;
                }
                else
                {
                    DicomLogger.Warning("PrintSCP", "图像 {Position} 没有像素数据", position);
                }
            }

            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.Success));
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-SET 请求失败");
            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try 
        {
            DicomLogger.Information("PrintSCP", "收到 N-ACTION 请求 - SOP Class: {SopClass}, Calling AE: {CallingAE}", 
                request.SOPClassUID?.Name ?? "Unknown", 
                _session.CallingAE ?? "Unknown");

            if (_session.FilmSession == null || _session.CurrentFilmBox == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session或Film Box为空，无法处理 N-ACTION 请求");
                return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            // 获取胶片参数
            var filmBox = _session.CurrentFilmBox;
            var filmBoxDataset = filmBox?.Dataset;
            if (filmBox == null || filmBoxDataset == null)
            {
                return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            var filmOrientation = filmBoxDataset.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT");
            var filmSizeID = filmBoxDataset.GetSingleValueOrDefault(DicomTag.FilmSizeID, "14INX17IN");
            var displayFormat = filmBoxDataset.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1");
            var filmBoxId = filmBox.SOPInstanceUID;

            DicomLogger.Information("PrintSCP", "胶片参数 - 方向: {Orientation}, 尺寸: {Size}", filmOrientation, filmSizeID);

            if (!FilmSizes.TryGetValue(filmSizeID, out var filmSize))
            {
                DicomLogger.Warning("PrintSCP", "不支持的胶片尺寸: {Size}", filmSizeID);
                return new DicomNActionResponse(request, DicomStatus.InvalidAttributeValue);
            }

            // 根据方向调整胶片尺寸
            var (filmWidth, filmHeight) = filmOrientation == "LANDSCAPE" 
                ? (filmSize.height, filmSize.width) 
                : (filmSize.width, filmSize.height);

            // 获取布局格式
            var layoutParts = displayFormat.Split('\\')[1].Split(',');
            if (layoutParts.Length != 2 || !int.TryParse(layoutParts[0], out int columns) || !int.TryParse(layoutParts[1], out int rows))
            {
                DicomLogger.Warning("PrintSCP", "无效的布局格式: {Format}", displayFormat);
                return new DicomNActionResponse(request, DicomStatus.InvalidAttributeValue);
            }

            // 计算基本参数
            var totalSlots = rows * columns;
            var imageWidth = filmWidth / columns;
            var imageHeight = filmHeight / rows;
            var firstImage = _session.CachedImages.First().Value;

            // 创建输出数据集
            var outputDataset = CreateOutputDataset(firstImage, filmWidth, filmHeight);
            var pixels = new byte[filmWidth * filmHeight];

            // 处理每个位置
            for (int position = 1; position <= totalSlots; position++)
            {
                int row = (position - 1) / columns;
                int col = (position - 1) % columns;
                int xBase = col * imageWidth;
                int yBase = row * imageHeight;

                if (_session.CachedImages.TryGetValue(position, out var image))
                {
                    ProcessImage(image, pixels, xBase, yBase, imageWidth, imageHeight, filmWidth);
                }
                else
                {
                    DrawBorder(pixels, xBase, yBase, imageWidth, imageHeight, filmWidth, (byte)128);
                }
            }

            // 保存结果
            outputDataset.AddOrUpdate(DicomTag.PixelData, pixels);
            await SaveResult(outputDataset, filmBoxId);

            return CreateResponse(request);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理打印请求时发生错误");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private DicomDataset CreateOutputDataset(DicomDataset sourceImage, int width, int height)
    {
        var dataset = new DicomDataset();
        dataset.AddOrUpdate(DicomTag.Rows, (ushort)height);
        dataset.AddOrUpdate(DicomTag.Columns, (ushort)width);
        dataset.AddOrUpdate(DicomTag.BitsAllocated, sourceImage.GetSingleValue<ushort>(DicomTag.BitsAllocated));
        dataset.AddOrUpdate(DicomTag.BitsStored, sourceImage.GetSingleValue<ushort>(DicomTag.BitsStored));
        dataset.AddOrUpdate(DicomTag.HighBit, sourceImage.GetSingleValue<ushort>(DicomTag.HighBit));
        dataset.AddOrUpdate(DicomTag.PixelRepresentation, sourceImage.GetSingleValue<ushort>(DicomTag.PixelRepresentation));
        dataset.AddOrUpdate(DicomTag.SamplesPerPixel, sourceImage.GetSingleValue<ushort>(DicomTag.SamplesPerPixel));
        dataset.AddOrUpdate(DicomTag.PhotometricInterpretation, sourceImage.GetSingleValue<string>(DicomTag.PhotometricInterpretation));
        
        var studyUid = sourceImage.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, _session.FilmSession?.SOPInstanceUID) 
            ?? DicomUID.Generate().UID;
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);

        // 检查 CurrentFilmBox 和 SOPInstanceUID
        var sopInstanceUid = _session.CurrentFilmBox?.SOPInstanceUID ?? DicomUID.Generate().UID;
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
        dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        dataset.AddOrUpdate(DicomTag.InstanceNumber, "1");

        return dataset;
    }

    private void ProcessImage(DicomDataset image, byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth)
    {
        try
        {
            var pixelData = image.GetValues<byte>(DicomTag.PixelData);
            var srcWidth = image.GetSingleValue<ushort>(DicomTag.Columns);
            var srcHeight = image.GetSingleValue<ushort>(DicomTag.Rows);

            // 验证源图像尺寸
            if (srcWidth == 0 || srcHeight == 0 || pixelData == null || pixelData.Length == 0)
            {
                DicomLogger.Warning("PrintSCP", "无效的源图像尺寸或像素数据");
                DrawBorder(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, (byte)128);
                return;
            }

            // 计算缩放比例，确保不会出现0的情况
            var scaleRatio = Math.Min(
                (double)(maxWidth - 10) / srcWidth,
                (double)(maxHeight - 10) / srcHeight
            );
            scaleRatio = Math.Max(scaleRatio, 0.001); // 防止缩放比例为0

            var scaledWidth = Math.Max((int)(srcWidth * scaleRatio), 1);
            var scaledHeight = Math.Max((int)(srcHeight * scaleRatio), 1);

            // 确保缩放后的尺寸不超过最大限制
            scaledWidth = Math.Min(scaledWidth, maxWidth);
            scaledHeight = Math.Min(scaledHeight, maxHeight);

            // 计算居中偏移
            var xOffset = xBase + (maxWidth - scaledWidth) / 2;
            var yOffset = yBase + (maxHeight - scaledHeight) / 2;

            // 复制像素时进行边界检查
            for (int y = 0; y < scaledHeight; y++)
            {
                for (int x = 0; x < scaledWidth; x++)
                {
                    int srcX = (int)(x / scaleRatio);
                    int srcY = (int)(y / scaleRatio);

                    // 确保源坐标在有效范围内
                    if (srcX >= 0 && srcX < srcWidth && srcY >= 0 && srcY < srcHeight)
                    {
                        int srcIndex = srcY * srcWidth + srcX;
                        int dstIndex = (yOffset + y) * filmWidth + (xOffset + x);

                        // 确保目标索引在有效范围内
                        if (srcIndex >= 0 && srcIndex < pixelData.Length && 
                            dstIndex >= 0 && dstIndex < pixels.Length)
                        {
                            pixels[dstIndex] = pixelData[srcIndex];
                        }
                    }
                }
            }

            // 绘制边框
            DrawBorder(pixels, xOffset, yOffset, scaledWidth, scaledHeight, filmWidth, (byte)255);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理图像时发生错误");
            // 发生错误时绘制边框表示该位置的图像处理失败
            DrawBorder(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, (byte)128);
        }
    }

    private async Task SaveResult(DicomDataset dataset, string filmBoxId)
    {
        var dateFolder = DateTime.Now.ToString("yyyyMMdd");
        var filename = $"{filmBoxId}.dcm";
        var relativePath = Path.Combine(_relativePrintPath, dateFolder, filename);
        var fullPath = Path.Combine(_settings.StoragePath, relativePath);

        // 检查并创建目录
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await new DicomFile(dataset).SaveAsync(fullPath);

        if (_session.FilmSession?.SOPInstanceUID != null)
        {
            await _repository.UpdatePrintJobAsync(
                _session.FilmSession.SOPInstanceUID,
                parameters: new Dictionary<string, object>
                {
                    ["ImagePath"] = relativePath,
                    ["Status"] = PrintJobStatus.ImageReceived.ToString(),
                    ["StudyInstanceUID"] = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID)
                });
        }
    }

    private DicomNActionResponse CreateResponse(DicomNActionRequest request)
    {
        var response = new DicomNActionResponse(request, DicomStatus.Success);
        var command = new DicomDataset
        {
            { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
            { DicomTag.CommandField, (ushort)0x8130 },
            { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
            { DicomTag.CommandDataSetType, (ushort)0x0101 },
            { DicomTag.Status, (ushort)DicomStatus.Success.Code },
            { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID },
            { DicomTag.ActionTypeID, request.ActionTypeID }
        };

        SetCommandDataset(response, command);
        return response;
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        try
        {
            var response = new DicomNDeleteResponse(request, DicomStatus.Success);
            
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8150 },  // N-DELETE-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            var commandProperty = typeof(DicomMessage).GetProperty("Command");
            commandProperty?.SetValue(response, command);

            // 根据不同的 SOP Class 清会话状态
            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                DicomLogger.Information("PrintSCP", "删除 Film Session: {Uid}", 
                    request.SOPInstanceUID?.UID ?? "Unknown");
                _session.FilmSession = null;
                _session.CurrentFilmBox = null;
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                DicomLogger.Information("PrintSCP", "删除 Film Box: {Uid}", 
                    request.SOPInstanceUID?.UID ?? "Unknown");
                _session.CurrentFilmBox = null;
            }

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-DELETE 请求失败");
            return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.Success));
    }

    private void SetCommandDataset(DicomResponse response, DicomDataset command)
    {
        var commandProperty = typeof(DicomMessage).GetProperty("Command");
        commandProperty?.SetValue(response, command);
    }

    private void CleanupSession()
    {
        if (_session.CurrentFilmBox != null)
        {
            DicomLogger.Information("PrintSCP", "清理 Film Box: {Uid}", 
                _session.CurrentFilmBox.SOPInstanceUID);
        }
        if (_session.FilmSession != null)
        {
            DicomLogger.Information("PrintSCP", "清理 Film Session: {Uid}", 
                _session.FilmSession.SOPInstanceUID);
        }

        _session = new PrintSession();
    }

    private void DrawBorder(byte[] pixels, int x, int y, int width, int height, int filmWidth, byte color)
    {
        try
        {
            // 确保边框在有效范围内
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            width = Math.Min(width, filmWidth - x);
            height = Math.Min(height, pixels.Length / filmWidth - y);

            // 绘制上下边框
            for (int i = 0; i < width; i++)
            {
                int top = y * filmWidth + (x + i);
                int bottom = (y + height - 1) * filmWidth + (x + i);
                
                if (top >= 0 && top < pixels.Length)
                {
                    pixels[top] = color;
                }
                if (bottom >= 0 && bottom < pixels.Length)
                {
                    pixels[bottom] = color;
                }
            }

            // 绘制左右边框
            for (int i = 0; i < height; i++)
            {
                int left = (y + i) * filmWidth + x;
                int right = (y + i) * filmWidth + (x + width - 1);
                
                if (left >= 0 && left < pixels.Length)
                {
                    pixels[left] = color;
                }
                if (right >= 0 && right < pixels.Length)
                {
                    pixels[right] = color;
                }
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "绘制边框时发生错误");
        }
    }
}

public class DicomFilmSession
{
    public DicomFilmSession(DicomDataset? dataset)
    {
    }
    public string SOPInstanceUID { get; set; } = "";
}

public class DicomFilmBox
{
    public DicomFilmBox(DicomDataset? dataset)
    {
        Dataset = dataset;
    }
    public string SOPInstanceUID { get; set; } = "";
    public DicomDataset? Dataset { get; set; }
} 