using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using DicomSCP.Configuration;
using Microsoft.Extensions.Options;
using DicomSCP.Models;
using Microsoft.Extensions.Logging;

namespace DicomSCP.Services;

public interface IPrintSCU
{
    Task<bool> PrintAsync(PrintRequest request);
    Task<bool> VerifyAsync(string hostName, int port, string calledAE);
}

public class PrintSCU : IPrintSCU
{
    private readonly DicomSettings _settings;
    private readonly string _aeTitle;
    private readonly ILoggerFactory _loggerFactory;

    public PrintSCU(IOptions<DicomSettings> settings, ILoggerFactory loggerFactory)
    {
        _settings = settings.Value;
        _aeTitle = _settings.PrintSCU?.AeTitle ?? "PRINTSCU";
        _loggerFactory = loggerFactory;
    }

    private IDicomClient CreateClient(string hostName, int port, string callingAE, string calledAE)
    {
        var client = DicomClientFactory.Create(hostName, port, false, callingAE, calledAE);
        client.NegotiateAsyncOps();
        return client;
    }

    public async Task<bool> VerifyAsync(string hostName, int port, string calledAE)
    {
        try
        {
            var client = CreateClient(hostName, port, _aeTitle, calledAE);
            var echo = new DicomCEchoRequest();
            await client.AddRequestAsync(echo);
            await client.SendAsync();
            return true;
        }
        catch (DicomAssociationRejectedException ex)
        {
            // 处理连接被拒绝的情况
            DicomLogger.Warning("PrintSCU", 
                "打印机拒绝连接 - {CalledAE}@{Host}:{Port}, 原因: {Reason}", 
                calledAE, hostName, port, ex.RejectResult);
            return false;
        }
        catch (Exception ex)
        {
            // 处理其他错误
            DicomLogger.Warning("PrintSCU", 
                "连接打印机失败 - {CalledAE}@{Host}:{Port}, 错误: {Error}", 
                calledAE, hostName, port, ex.Message);
            return false;
        }
    }

    public async Task<bool> PrintAsync(PrintRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.CallingAE))
            {
                request.CallingAE = _aeTitle;
            }

            // 先验证连接
            if (!await VerifyAsync(request.HostName, request.Port, request.CalledAE))
            {
                DicomLogger.Error("PrintSCU", "打印机连接验证失败");
                return false;
            }

            DicomLogger.Information("PrintSCU", "开始打印任务 - 从 {CallingAE} 到 {CalledAE}@{Host}:{Port}", 
                request.CallingAE,
                request.CalledAE, 
                request.HostName, 
                request.Port);

            // 读取源文件
            var file = await DicomFile.OpenAsync(request.FilePath);

            // 创建打印客户端
            var client = CreateClient(
                request.HostName, 
                request.Port, 
                request.CallingAE,
                request.CalledAE);

            // 添加打印相关的表示上下文
            client.AdditionalPresentationContexts.Add(
                DicomPresentationContext.GetScpRolePresentationContext(
                    DicomUID.BasicFilmSession));
            client.AdditionalPresentationContexts.Add(
                DicomPresentationContext.GetScpRolePresentationContext(
                    DicomUID.BasicFilmBox));
            client.AdditionalPresentationContexts.Add(
                DicomPresentationContext.GetScpRolePresentationContext(
                    DicomUID.BasicGrayscaleImageBox));

            // 创建打印会话
            var filmSessionUid = DicomUID.Generate();
            var filmBoxUid = DicomUID.Generate();

            // 创建Film Session
            var filmSessionRequest = new DicomNCreateRequest(
                DicomUID.BasicFilmSession,
                filmSessionUid)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.NumberOfCopies, (ushort)request.NumberOfCopies },
                    { DicomTag.PrintPriority, request.PrintPriority },
                    { DicomTag.MediumType, request.MediumType },
                    { DicomTag.FilmDestination, request.FilmDestination }
                }
            };

            // 创建Film Box
            var filmBoxRequest = new DicomNCreateRequest(
                DicomUID.BasicFilmBox,
                filmBoxUid)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.ImageDisplayFormat, request.ImageDisplayFormat },
                    { DicomTag.FilmOrientation, request.FilmOrientation },
                    { DicomTag.FilmSizeID, request.FilmSizeID },
                    { DicomTag.MagnificationType, request.MagnificationType },
                    { DicomTag.SmoothingType, request.SmoothingType },
                    { DicomTag.BorderDensity, request.BorderDensity },
                    { DicomTag.EmptyImageDensity, request.EmptyImageDensity },
                    { DicomTag.Trim, request.Trim }
                }
            };

            // 创建Image Box
            var imageBoxRequest = new DicomNSetRequest(
                DicomUID.BasicGrayscaleImageBox,
                DicomUID.Generate())
            {
                Dataset = file.Dataset
            };

            // 发送请求序列
            await client.AddRequestAsync(filmSessionRequest);
            await client.AddRequestAsync(filmBoxRequest);
            await client.AddRequestAsync(imageBoxRequest);

            // 发送打印命令
            var printRequest = new DicomNActionRequest(
                DicomUID.BasicFilmSession,
                filmSessionUid,
                1);
            await client.AddRequestAsync(printRequest);

            // 执行打印
            await client.SendAsync();

            DicomLogger.Information("PrintSCU", "打印任务完成");
            return true;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "打印过程中发生错误");
            return false;
        }
    }
} 