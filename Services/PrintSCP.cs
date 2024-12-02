using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Data;
using DicomSCP.Models;

namespace DicomSCP.Services;

public class PrintSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private static DicomSettings? _settings;
    private static DicomRepository? _repository;

    public static void Configure(
        DicomSettings settings,
        DicomRepository repository)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly string _printPath;

    public PrintSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        Microsoft.Extensions.Logging.ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        if (_settings == null || _repository == null)
        {
            throw new InvalidOperationException("PrintSCP not configured");
        }

        _printPath = Path.Combine(_settings.StoragePath, "prints");
        Directory.CreateDirectory(_printPath);
    }

    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        DicomLogger.Information("PrintSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
            association.CalledAE, association.CallingAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification ||
                pc.AbstractSyntax == DicomUID.BasicFilmSession ||
                pc.AbstractSyntax == DicomUID.BasicFilmBox ||
                pc.AbstractSyntax == DicomUID.BasicGrayscaleImageBox ||
                pc.AbstractSyntax == DicomUID.BasicColorImageBox ||
                pc.AbstractSyntax == DicomUID.Printer)
            {
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                DicomLogger.Information("PrintSCP", "接受打印服务 - {Service}", pc.AbstractSyntax.Name);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                DicomLogger.Warning("PrintSCP", "拒绝不支持的服务 - {Service}", pc.AbstractSyntax.Name);
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("PrintSCP", "接收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("PrintSCP", "接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("PrintSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("PrintSCP", "连接正常关闭");
        }
    }

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-ACTION 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNActionResponse(request, DicomStatus.Success));
    }

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-CREATE 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            if (request.SOPClassUID == DicomUID.BasicFilmSession)
            {
                if (_repository == null)
                {
                    throw new InvalidOperationException("PrintSCP repository not configured");
                }

                // 创建打印任务
                var job = new PrintJob
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    FilmSessionId = request.SOPInstanceUID.UID,
                    CallingAE = Association.CallingAE,
                    Status = "PENDING",
                    CreateTime = DateTime.UtcNow
                };

                await _repository.AddPrintJobAsync(job);
            }

            return new DicomNCreateResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-DELETE 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.Success));
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-EVENT-REPORT 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 N-GET 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);
        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.Success));
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            DicomLogger.Information("PrintSCP", "收到 N-SET 请求 - SOP Class: {SopClass}", request.SOPClassUID.Name);

            if (request.SOPClassUID == DicomUID.BasicGrayscaleImageBox ||
                request.SOPClassUID == DicomUID.BasicColorImageBox)
            {
                if (_repository == null)
                {
                    throw new InvalidOperationException("PrintSCP repository not configured");
                }

                // 保存打印图像
                var imageData = request.Dataset.GetDicomItem<DicomItem>(DicomTag.PixelData);
                if (imageData != null)
                {
                    var imagePath = Path.Combine(_printPath, $"{request.SOPInstanceUID.UID}.dcm");
                    var dicomFile = new DicomFile(request.Dataset);
                    await dicomFile.SaveAsync(imagePath);

                    // 更新打印任务
                    await _repository.UpdatePrintJobStatusAsync(
                        request.SOPInstanceUID.UID,
                        "PRINTING",
                        imagePath
                    );
                }
            }

            return new DicomNSetResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCP", ex, "处理 N-SET 请求失败");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("PrintSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }
} 