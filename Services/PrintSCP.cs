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
        ["14INX17IN"] = (2100, 2550),  // 14x17英寸 (14*150=2100, 17*150=2550)
        ["11INX14IN"] = (1650, 2100),  // 11x14英寸 (11*150=1650, 14*150=2100)
        ["8INX10IN"] = (1200, 1500),   // 8x10英寸 (8*150=1200, 10*150=1500)
        ["10INX12IN"] = (1500, 1800),  // 10x12英寸 (10*150=1500, 12*150=1800)
        ["24CMX30CM"] = (1417, 1772),  // 24x30厘米 (24*59=1417, 30*59=1772)
        ["24CMX24CM"] = (1417, 1417),  // 24x24厘米 (24*59=1417)
        ["A4"] = (1240, 1754),         // A4 (210x297mm = 8.27x11.69英寸 -> 1240x1754)
        ["A3"] = (1754, 2480)          // A3 (297x420mm = 11.69x16.53英寸 -> 1754x2480)
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

            DicomLogger.Warning("PrintSCP", "Unsupported SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
            return new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Failed to process N-CREATE request");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmSessionCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "Creating Film Session - Calling AE: {CallingAE}", _session.CallingAE ?? "Unknown");

            // Generate FilmSessionId, independent of SOPInstanceUID
            var filmSessionId = DicomUID.Generate().UID;
            var printJob = new PrintJob
            {
                JobId = $"{DateTime.Now:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}",
                FilmSessionId = filmSessionId,  // Use generated ID
                CallingAE = _session.CallingAE ?? "",
                Status = PrintJobStatus.Created,

                // Film Session parameters
                NumberOfCopies = request.Dataset?.GetSingleValueOrDefault(DicomTag.NumberOfCopies, 1) ?? 1,
                PrintPriority = request.Dataset?.GetSingleValueOrDefault(DicomTag.PrintPriority, "LOW") ?? "LOW",
                MediumType = request.Dataset?.GetSingleValueOrDefault(DicomTag.MediumType, "BLUE FILM") ?? "BLUE FILM",
                FilmDestination = request.Dataset?.GetSingleValueOrDefault(DicomTag.FilmDestination, "MAGAZINE") ?? "MAGAZINE",

                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };

            // Save to database
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

            DicomLogger.Information("PrintSCP", "Film Session created successfully - ID: {Id}", filmSessionId);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Failed to handle Film Session creation");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNCreateResponse> HandleFilmBoxCreateAsync(DicomNCreateRequest request)
    {
        try
        {
            if (_session.FilmSession == null)
            {
                DicomLogger.Warning("PrintSCP", "Cannot create Film Box - Film Session does not exist");
                return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            DicomLogger.Information("PrintSCP", "Creating Film Box - Film Session: {SessionId}, Calling AE: {CallingAE}",
                _session.FilmSession.SOPInstanceUID,
                _session.CallingAE ?? "Unknown");

            // Generate FilmBoxId
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

            // Copy attributes from the original request
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

            // Get layout format
            var imageDisplayFormat = request.Dataset?.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1") ?? "STANDARD\\1,1";
            var layoutParts = imageDisplayFormat.Split('\\')[1].Split(',');
            if (layoutParts.Length != 2 || !int.TryParse(layoutParts[0], out int columns) || !int.TryParse(layoutParts[1], out int rows))
            {
                DicomLogger.Warning("PrintSCP", "Invalid layout format: {Format}", imageDisplayFormat);
                return new DicomNCreateResponse(request, DicomStatus.InvalidAttributeValue);
            }

            DicomLogger.Information("PrintSCP", "Layout: {Columns} columns x {Rows} rows", columns, rows);

            // Create Image Box references based on layout
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
                DicomLogger.Information("PrintSCP", "Created Image Box {Position}/{Total}", i, totalBoxes);
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
                { DicomTag.AffectedSOPInstanceUID, filmBoxId }  // Use generated filmBoxId
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
            DicomLogger.Error("PrintSCP", ex, "Failed to handle Film Box creation");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "Received N-SET request - SOP Class: {SopClass}, Calling AE: {CallingAE}",
                request.SOPClassUID?.Name ?? "Unknown",
                _session.CallingAE ?? "Unknown");

            if (_session.FilmSession == null || _session.CurrentFilmBox == null)
            {
                DicomLogger.Warning("PrintSCP", "Film Session or Film Box is null, cannot process N-SET request");
                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance));
            }

            // Get image sequence
            var imageSequence = request.Dataset?.GetSequence(DicomTag.BasicGrayscaleImageSequence);
            if (imageSequence == null || !imageSequence.Items.Any())
            {
                DicomLogger.Warning("PrintSCP", "No image sequence found or sequence is empty");
                return Task.FromResult(new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance));
            }

            // Get image position from SOPInstanceUID
            var sopInstanceUid = request.SOPInstanceUID?.UID ?? "";
            var position = 1;
            var parts = sopInstanceUid.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[parts.Length - 1], out int pos))
            {
                position = pos;
            }

            DicomLogger.Information("PrintSCP", "Processing image - SOPInstanceUID: {Uid}, Position: {Position}", sopInstanceUid, position);

            // Process received images
            foreach (var item in imageSequence.Items)
            {
                if (item.Contains(DicomTag.PixelData))
                {
                    var pixelData = item.GetValues<byte>(DicomTag.PixelData);
                    DicomLogger.Information("PrintSCP", "Image {Position} - Pixel data size: {Size} bytes", position, pixelData?.Length ?? 0);
                    _session.CachedImages[position] = item;
                }
                else
                {
                    DicomLogger.Warning("PrintSCP", "Image {Position} has no pixel data", position);
                }
            }

            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.Success));
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Failed to process N-SET request");
            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try
        {
            if (_session.CurrentFilmBox?.Dataset == null)
            {
                return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            // Get and validate layout format
            var displayFormat = _session.CurrentFilmBox.Dataset.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1");
            var layoutParts = displayFormat.Split('\\')[1].Split(',');
            if (layoutParts.Length != 2 || !int.TryParse(layoutParts[0], out int columns) || !int.TryParse(layoutParts[1], out int rows))
            {
                DicomLogger.Warning("PrintSCP", "Invalid layout format: {Format}", displayFormat);
                return new DicomNActionResponse(request, DicomStatus.InvalidAttributeValue);
            }

            // Get film size
            var filmSizeID = _session.CurrentFilmBox.Dataset.GetSingleValueOrDefault(DicomTag.FilmSizeID, "14INX17IN");
            var filmOrientation = _session.CurrentFilmBox.Dataset.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT");

            var filmSize = GetFilmSize(filmSizeID);
            var (filmWidth, filmHeight) = filmOrientation.Equals("LANDSCAPE", StringComparison.OrdinalIgnoreCase)
                ? (filmSize.height, filmSize.width)
                : (filmSize.width, filmSize.height);

            // Calculate dividing lines
            const int lineWidth = 1;  // Line width
            var totalSlots = rows * columns;
            var isSingleImage = totalSlots == 1;  // Check if it's a single image layout

            // Calculate each image size (excluding dividing lines)
            var imageWidth = isSingleImage ? filmWidth : (filmWidth - (columns - 1) * lineWidth) / columns;
            var imageHeight = isSingleImage ? filmHeight : (filmHeight - (rows - 1) * lineWidth) / rows;

            // Create output dataset
            var outputDataset = CreateOutputDataset(_session.CachedImages.First().Value, filmWidth, filmHeight);
            var pixels = new byte[filmWidth * filmHeight];

            // Process each position
            for (int position = 1; position <= totalSlots; position++)
            {
                int row = (position - 1) / columns;
                int col = (position - 1) % columns;

                // Calculate image position (considering dividing lines)
                int xBase = isSingleImage ? 0 : col * (imageWidth + lineWidth);
                int yBase = isSingleImage ? 0 : row * (imageHeight + lineWidth);

                if (_session.CachedImages.TryGetValue(position, out var image))
                {
                    ProcessImage(image, pixels, xBase, yBase, imageWidth, imageHeight, filmWidth, isSingleImage);
                }
                else
                {
                    DrawEmptyImage(pixels, xBase, yBase, imageWidth, imageHeight, filmWidth, isSingleImage);
                }
            }

            // Draw dividing lines and borders only in multi-image layout
            if (!isSingleImage)
            {
                // Draw vertical lines (including left and right borders)
                for (int i = 0; i <= columns; i++)
                {
                    int x = i * (imageWidth + lineWidth) - lineWidth;
                    if (i == 0) x = 0;  // Left border
                    if (i == columns) x = filmWidth - 1;  // Right border

                    for (int y = 0; y < filmHeight; y++)
                    {
                        var index = y * filmWidth + x;
                        if (index < pixels.Length)
                        {
                            pixels[index] = 255;  // White line
                        }
                    }
                }

                // Draw horizontal lines (including top and bottom borders)
                for (int i = 0; i <= rows; i++)
                {
                    int y = i * (imageHeight + lineWidth) - lineWidth;
                    if (i == 0) y = 0;  // Top border
                    if (i == rows) y = filmHeight - 1;  // Bottom border

                    for (int x = 0; x < filmWidth; x++)
                    {
                        var index = y * filmWidth + x;
                        if (index < pixels.Length)
                        {
                            pixels[index] = 255;  // White line
                        }
                    }
                }
            }

            // Save result
            outputDataset.AddOrUpdate(DicomTag.PixelData, pixels);
            await SaveResult(outputDataset, request.SOPInstanceUID?.UID ?? _session.CurrentFilmBox.SOPInstanceUID);

            return CreateResponse(request);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error processing print request");
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

        // Check CurrentFilmBox and SOPInstanceUID
        var sopInstanceUid = _session.CurrentFilmBox?.SOPInstanceUID ?? DicomUID.Generate().UID;
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
        dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        dataset.AddOrUpdate(DicomTag.InstanceNumber, "1");

        return dataset;
    }

    private void ProcessImage(DicomDataset image, byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth, bool isSingleImage)
    {
        try
        {
            if (image == null || !image.Contains(DicomTag.PixelData))
            {
                DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
                return;
            }

            var pixelData = image.GetValues<byte>(DicomTag.PixelData);
            var srcWidth = image.GetSingleValue<ushort>(DicomTag.Columns);
            var srcHeight = image.GetSingleValue<ushort>(DicomTag.Rows);

            if (srcWidth == 0 || srcHeight == 0 || pixelData == null || pixelData.Length == 0)
            {
                DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
                return;
            }

            // Add margins only in multi-image layout
            double marginPercent = isSingleImage ? 0 : 0.02;
            var marginX = (int)(maxWidth * marginPercent);
            var marginY = (int)(maxHeight * marginPercent);
            var availableWidth = maxWidth - 2 * marginX;
            var availableHeight = maxHeight - 2 * marginY;

            // Calculate scaling factor and maintain original aspect ratio
            var scale = Math.Min((double)availableWidth / srcWidth, (double)availableHeight / srcHeight);
            var scaledWidth = (int)(srcWidth * scale);
            var scaledHeight = (int)(srcHeight * scale);

            // Center the image (considering margins)
            var xOffset = xBase + marginX + (availableWidth - scaledWidth) / 2;
            var yOffset = yBase + marginY + (availableHeight - scaledHeight) / 2;

            // Copy and scale the image
            for (int y = 0; y < scaledHeight; y++)
            {
                var srcY = Math.Min((int)(y / scale), srcHeight - 1);
                var dstRowOffset = (yOffset + y) * filmWidth;
                var srcRowOffset = srcY * srcWidth;

                for (int x = 0; x < scaledWidth; x++)
                {
                    var srcX = Math.Min((int)(x / scale), srcWidth - 1);
                    var dstIndex = dstRowOffset + xOffset + x;
                    var srcIndex = srcRowOffset + srcX;

                    if (srcIndex < pixelData.Length && dstIndex < pixels.Length)
                    {
                        pixels[dstIndex] = pixelData[srcIndex];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error processing image");
            DrawEmptyImage(pixels, xBase, yBase, maxWidth, maxHeight, filmWidth, isSingleImage);
        }
    }

    private void DrawEmptyImage(byte[] pixels, int xBase, int yBase, int maxWidth, int maxHeight, int filmWidth, bool isSingleImage)
    {
        // Add margins only in multi-image layout
        double marginPercent = isSingleImage ? 0 : 0.02;
        var marginX = (int)(maxWidth * marginPercent);
        var marginY = (int)(maxHeight * marginPercent);

        // Fill with black (considering margins)
        for (int y = 0; y < maxHeight; y++)
        {
            var rowOffset = (yBase + y) * filmWidth;
            for (int x = 0; x < maxWidth; x++)
            {
                var index = rowOffset + xBase + x;
                if (index < pixels.Length)
                {
                    // Fill with black only in the inner area
                    if (y >= marginY && y < (maxHeight - marginY) &&
                        x >= marginX && x < (maxWidth - marginX))
                    {
                        pixels[index] = 0;  // Black
                    }
                }
            }
        }
    }
    private async Task SaveResult(DicomDataset dataset, string filmBoxId)
    {
        try
        {
            var dateFolder = DateTime.Now.ToString("yyyyMMdd");
            var filename = $"{filmBoxId}.dcm";
            var relativePath = Path.Combine(_relativePrintPath, dateFolder, filename);
            var fullPath = Path.Combine(_settings.StoragePath, relativePath);

            // Ensure directory exists
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // Log save information
            DicomLogger.Information("PrintSCP", "Saving print result - ID: {Id}, Path: {Path}",
                filmBoxId, relativePath);

            await new DicomFile(dataset).SaveAsync(fullPath);

            if (_session.FilmSession?.SOPInstanceUID != null)
            {
                await _repository.UpdatePrintJobAsync(
                    _session.FilmSession.SOPInstanceUID,
                    parameters: new Dictionary<string, object>
                    {
                        ["ImagePath"] = relativePath,
                        ["Status"] = PrintJobStatus.ImageReceived.ToString(),
                        ["StudyInstanceUID"] = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
                        ["UpdateTime"] = DateTime.Now
                    });
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error saving print result");
            throw;
        }
    }

    private DicomNActionResponse CreateResponse(DicomNActionRequest request)
    {
        try
        {
            var response = new DicomNActionResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // Required command set elements (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8130 },  // N-ACTION-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // No dataset
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID },
                { DicomTag.ActionTypeID, request.ActionTypeID }
            };

            SetCommandDataset(response, command);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error creating response");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private void SetCommandDataset(DicomResponse response, DicomDataset command)
    {
        try
        {
            var commandProperty = typeof(DicomMessage).GetProperty("Command",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);

            if (commandProperty != null)
            {
                commandProperty.SetValue(response, command);
            }
            else
            {
                DicomLogger.Error("PrintSCP", "Unable to set command dataset: Command property does not exist");
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error setting command dataset");
        }
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        try
        {
            // Validate request
            if (request.SOPClassUID == null || request.SOPInstanceUID?.UID == null)
            {
                DicomLogger.Warning("PrintSCP", "Invalid request parameters");
                return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.InvalidArgumentValue));
            }

            var response = new DicomNDeleteResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // Required command set elements (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8150 },  // N-DELETE-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // No dataset
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            SetCommandDataset(response, command);

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                if (_session.FilmSession?.SOPInstanceUID != request.SOPInstanceUID.UID)
                {
                    return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance));
                }

                DicomLogger.Information("PrintSCP", "Deleting Film Session: {Uid}", request.SOPInstanceUID.UID);
                _session.FilmSession = null;
                _session.CurrentFilmBox = null;
                _session.CachedImages.Clear();
            }
            else if (request.SOPClassUID == DicomUID.BasicFilmBox)
            {
                if (_session.CurrentFilmBox?.SOPInstanceUID != request.SOPInstanceUID.UID)
                {
                    return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance));
                }

                DicomLogger.Information("PrintSCP", "Deleting Film Box: {Uid}", request.SOPInstanceUID.UID);
                _session.CurrentFilmBox = null;
                _session.CachedImages.Clear();
            }
            else
            {
                return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.NoSuchSOPClass));
            }

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error processing N-DELETE request");
            return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        try
        {
            var response = new DicomNEventReportResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // Required command set elements (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8110 },  // N-EVENT-REPORT-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // No dataset
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID },
                { DicomTag.EventTypeID, request.EventTypeID }
            };

            SetCommandDataset(response, command);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error processing N-EVENT-REPORT request");
            return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        try
        {
            var response = new DicomNGetResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                // Required command set elements (Type 1)
                { DicomTag.AffectedSOPClassUID, request.SOPClassUID },
                { DicomTag.CommandField, (ushort)0x8110 },  // N-GET-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },  // No dataset
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID }
            };

            SetCommandDataset(response, command);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "Error processing N-GET request");
            return Task.FromResult(new DicomNGetResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    private void CleanupSession()
    {
        if (_session.CurrentFilmBox != null)
        {
            DicomLogger.Information("PrintSCP", "Cleaning up Film Box: {Uid}",
                _session.CurrentFilmBox.SOPInstanceUID);
        }
        if (_session.FilmSession != null)
        {
            DicomLogger.Information("PrintSCP", "Cleaning up Film Session: {Uid}",
                _session.FilmSession.SOPInstanceUID);
        }

        _session = new PrintSession();
    }

    private (int width, int height) GetFilmSize(string sizeId)
    {
        return FilmSizes.TryGetValue(sizeId, out var size) ? size : FilmSizes["14INX17IN"];
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
