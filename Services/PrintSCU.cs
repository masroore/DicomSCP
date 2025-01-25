using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging;
using DicomSCP.Configuration;
using DicomSCP.Models;
using Microsoft.Extensions.Options;
using System.Numerics;

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

    // DICOM printing constants
    private static class PrintConstants
    {
        // DICOM standard values
        public static readonly string[] PrintPriorities = { "HIGH", "MED", "LOW" };
        public static readonly string[] MediumTypes = { "PAPER", "CLEAR FILM", "BLUE FILM" };
        public static readonly string[] FilmDestinations = { "MAGAZINE", "PROCESSOR", "BIN_1", "BIN_2" };
        public static readonly string[] FilmOrientations = { "PORTRAIT", "LANDSCAPE" };
        public static readonly string[] FilmSizes = {
            "8INX10IN", "10INX12IN", "11INX14IN", "14INX14IN",
            "14INX17IN", "24CMX30CM", "A4"
        };
        public static readonly string[] MagnificationTypes = {
            "REPLICATE", "BILINEAR", "CUBIC", "NONE"
        };
        public static readonly string[] SmoothingTypes = {
            "NONE", "MEDIUM", "SMOOTH"  // Corrected to standard values
        };
        public static readonly string[] Densities = { "BLACK", "WHITE" };
        public static readonly string[] TrimOptions = { "YES", "NO" };

        // Default values - using DICOM standard values
        public const string DefaultPriority = "MED";
        public const string DefaultMediumType = "BLUE FILM";
        public const string DefaultDestination = "PROCESSOR";
        public const string DefaultOrientation = "PORTRAIT";
        public const string DefaultSize = "14INX17IN";
        public const string DefaultDisplayFormat = "STANDARD\\1,1";
        public const string DefaultMagnification = "REPLICATE";
        public const string DefaultSmoothing = "MEDIUM";  // Corrected to standard values
        public const string DefaultDensity = "BLACK";
        public const string DefaultTrim = "NO";
    }

    public PrintSCU(IOptions<DicomSettings> settings)
    {
        _settings = settings.Value;
        _aeTitle = _settings.PrintSCU?.AeTitle ?? "PRINTSCU";
    }

    private IDicomClient CreateClient(string hostName, int port, string callingAE, string calledAE)
    {
        var client = DicomClientFactory.Create(hostName, port, false, callingAE, calledAE);
        client.NegotiateAsyncOps();
        return client;
    }

    private bool ValidateAETitle(string aeTitle)
    {
        // AE Title must be within 16 ASCII characters
        if (string.IsNullOrEmpty(aeTitle) || aeTitle.Length > 16)
            return false;

        // Only allow uppercase letters, numbers, and specific symbols
        return aeTitle.All(c => (c >= 'A' && c <= 'Z') ||
                               (c >= '0' && c <= '9') ||
                               c == '-' || c == '_');
    }

    private bool ValidateHostName(string hostName)
    {
        // Check if it is an IP address or a valid hostname
        return !string.IsNullOrEmpty(hostName) &&
               (System.Net.IPAddress.TryParse(hostName, out _) ||
                Uri.CheckHostName(hostName) != UriHostNameType.Unknown);
    }

    private bool ValidatePort(int port)
    {
        // Port number range: 1-65535
        return port > 0 && port <= 65535;
    }

    private void SetDefaultValues(PrintRequest request)
    {
        if (request.NumberOfCopies < 1)
            request.NumberOfCopies = 1;
        if (string.IsNullOrEmpty(request.PrintPriority))
            request.PrintPriority = PrintConstants.DefaultPriority;
        if (string.IsNullOrEmpty(request.MediumType))
            request.MediumType = PrintConstants.DefaultMediumType;
        if (string.IsNullOrEmpty(request.FilmDestination))
            request.FilmDestination = PrintConstants.DefaultDestination;
        if (string.IsNullOrEmpty(request.FilmOrientation))
            request.FilmOrientation = PrintConstants.DefaultOrientation;
        if (string.IsNullOrEmpty(request.FilmSizeID))
            request.FilmSizeID = PrintConstants.DefaultSize;
        if (string.IsNullOrEmpty(request.ImageDisplayFormat))
            request.ImageDisplayFormat = PrintConstants.DefaultDisplayFormat;
        if (string.IsNullOrEmpty(request.MagnificationType))
            request.MagnificationType = PrintConstants.DefaultMagnification;
        if (string.IsNullOrEmpty(request.SmoothingType))
            request.SmoothingType = PrintConstants.DefaultSmoothing;
        if (string.IsNullOrEmpty(request.BorderDensity))
            request.BorderDensity = PrintConstants.DefaultDensity;
        if (string.IsNullOrEmpty(request.EmptyImageDensity))
            request.EmptyImageDensity = PrintConstants.DefaultDensity;
        if (string.IsNullOrEmpty(request.Trim))
            request.Trim = PrintConstants.DefaultTrim;
    }

    private string NormalizeSmoothingType(string? smoothing)
    {
        if (string.IsNullOrEmpty(smoothing))
            return PrintConstants.DefaultSmoothing;

        switch (smoothing.ToUpper().Trim())
        {
            case "NONE":
                return "NONE";
            case "MEDIUM":
            case "MED":
                return "MEDIUM";
            case "SMOOTH":
            case "HIGH":
                return "SMOOTH";
            default:
                return PrintConstants.DefaultSmoothing;
        }
    }

    private bool ValidateConnectionParameters(string hostName, int port, string calledAE, string? errorSource = null)
    {
        if (!ValidateAETitle(calledAE))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "Invalid Called AE Title: {CalledAE}", calledAE);
            return false;
        }

        if (!ValidateHostName(hostName))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "Invalid hostname: {HostName}", hostName);
            return false;
        }

        if (!ValidatePort(port))
        {
            DicomLogger.Error(errorSource ?? "PrintSCU", "Invalid port number: {Port}", port);
            return false;
        }

        return true;
    }

    private bool ValidatePrintParameters(PrintRequest request)
    {
        // Validate file path
        if (string.IsNullOrEmpty(request.FilePath))
        {
            DicomLogger.Error("PrintSCU", "File path cannot be empty");
            return false;
        }

        if (!System.IO.File.Exists(request.FilePath))
        {
            DicomLogger.Error("PrintSCU", "File does not exist: {FilePath}", request.FilePath);
            return false;
        }

        // Validate connection parameters
        if (!ValidateConnectionParameters(request.HostName, request.Port, request.CalledAE))
            return false;

        // Normalize and validate print priority
        switch (request.PrintPriority?.ToUpper()?.Trim() ?? "")
        {
            case "HIGH":
            case "HI":
            case "H":
                request.PrintPriority = "HIGH";
                break;
            case "MEDIUM":
            case "MED":
            case "M":
                request.PrintPriority = "MED";
                break;
            case "LOW":
            case "LO":
            case "L":
                request.PrintPriority = "LOW";
                break;
            default:
                DicomLogger.Error("PrintSCU", "Invalid print priority: {Priority}",
                    request.PrintPriority ?? "NULL");
                return false;
        }

        // Normalize smoothing type
        request.SmoothingType = NormalizeSmoothingType(request.SmoothingType);

        // Validate print parameters
        if (!PrintConstants.MediumTypes.Contains(request.MediumType))
        {
            DicomLogger.Error("PrintSCU", "Invalid medium type: {MediumType}", request.MediumType);
            return false;
        }

        if (!PrintConstants.FilmOrientations.Contains(request.FilmOrientation))
        {
            DicomLogger.Error("PrintSCU", "Invalid film orientation: {Orientation}", request.FilmOrientation);
            return false;
        }

        if (!PrintConstants.FilmSizes.Contains(request.FilmSizeID))
        {
            DicomLogger.Error("PrintSCU", "Invalid film size: {Size}", request.FilmSizeID);
            return false;
        }

        if (!PrintConstants.MagnificationTypes.Contains(request.MagnificationType))
        {
            DicomLogger.Error("PrintSCU", "Invalid magnification type: {Type}", request.MagnificationType);
            return false;
        }

        if (!PrintConstants.SmoothingTypes.Contains(request.SmoothingType))
        {
            DicomLogger.Error("PrintSCU", "Invalid smoothing type: {Type}", request.SmoothingType);
            return false;
        }

        if (!PrintConstants.Densities.Contains(request.BorderDensity))
        {
            DicomLogger.Error("PrintSCU", "Invalid border density: {Density}", request.BorderDensity);
            return false;
        }

        if (!PrintConstants.Densities.Contains(request.EmptyImageDensity))
        {
            DicomLogger.Error("PrintSCU", "Invalid empty image density: {Density}", request.EmptyImageDensity);
            return false;
        }

        if (!PrintConstants.TrimOptions.Contains(request.Trim))
        {
            DicomLogger.Error("PrintSCU", "Invalid trim option: {Trim}", request.Trim);
            return false;
        }

        return true;
    }

    private async Task<DicomFile> LoadDicomFileAsync(string filePath)
    {
        DicomLogger.Information("PrintSCU", "Reading DICOM file - Path: {FilePath}, Size: {Size} bytes",
            filePath, new FileInfo(filePath).Length);
        return await DicomFile.OpenAsync(filePath);
    }

    private DicomDataset CreateFilmSessionDataset(PrintRequest request)
    {
        return new DicomDataset
        {
            { DicomTag.NumberOfCopies, request.NumberOfCopies.ToString() },
            { DicomTag.PrintPriority, request.PrintPriority },
            { DicomTag.MediumType, request.MediumType },
            { DicomTag.FilmDestination, request.FilmDestination }
        };
    }

    private DicomDataset CreateFilmBoxDataset(PrintRequest request)
    {
        return new DicomDataset
        {
            { DicomTag.ImageDisplayFormat, request.ImageDisplayFormat },
            { DicomTag.FilmOrientation, request.FilmOrientation },
            { DicomTag.FilmSizeID, request.FilmSizeID },
            { DicomTag.MagnificationType, request.MagnificationType },
            { DicomTag.SmoothingType, request.SmoothingType },
            { DicomTag.BorderDensity, request.BorderDensity },
            { DicomTag.EmptyImageDensity, request.EmptyImageDensity },
            { DicomTag.Trim, request.Trim }
        };
    }

    private DicomDataset CreateImageBoxDataset(DicomFile file)
    {
        // Create image dataset
        var imageDataset = new DicomDataset
    {
        { DicomTag.Columns, (ushort)file.Dataset.GetSingleValue<int>(DicomTag.Columns) },
        { DicomTag.Rows, (ushort)file.Dataset.GetSingleValue<int>(DicomTag.Rows) },
        { DicomTag.BitsAllocated, (ushort)8 },
        { DicomTag.BitsStored, (ushort)8 },
        { DicomTag.HighBit, (ushort)7 },
        { DicomTag.PixelRepresentation, (ushort)0 },
        { DicomTag.SamplesPerPixel, (ushort)1 },
        { DicomTag.PhotometricInterpretation, "MONOCHROME2" }
    };

        // Convert image
        var dicomImage = new DicomImage(file.Dataset);
        using var renderedImage = dicomImage.RenderImage();
        if (renderedImage is not IImage imageData)
        {
            throw new DicomDataException("Image conversion failed");
        }

        var pixelData = new byte[imageData.Width * imageData.Height];
        ConvertToGrayscale(renderedImage, pixelData, imageData.Width, imageData.Height);
        imageDataset.Add(DicomTag.PixelData, pixelData);

        // Create Image Box dataset
        return new DicomDataset
    {
        { DicomTag.ImageBoxPosition, (ushort)1 },
        { DicomTag.Polarity, "NORMAL" },
        { DicomTag.BasicGrayscaleImageSequence, new DicomDataset[] { imageDataset } }
    };
    }

    private static void ConvertToGrayscale(IImage renderedImage, byte[] pixelData, int width, int height)
    {
        var pixels = renderedImage.AsBytes();
        if (pixels == null || pixels.Length < width * height * 4)
        {
            throw new DicomDataException("Failed to get image data");
        }

        // Use SIMD optimized parallel processing
        var vectorSize = Vector<byte>.Count;
        var vectorCount = pixels.Length / (4 * vectorSize);

        Parallel.For(0, vectorCount, i =>
        {
            var offset = i * 4 * vectorSize;
            for (var j = 0; j < vectorSize; j++)
            {
                var pixelOffset = offset + j * 4;
                var r = pixels[pixelOffset];
                var g = pixels[pixelOffset + 1];
                var b = pixels[pixelOffset + 2];
                pixelData[i * vectorSize + j] = (byte)((r * 38 + g * 75 + b * 15) >> 7);
            }
        });

        // Process remaining pixels
        for (var i = vectorCount * vectorSize; i < width * height; i++)
        {
            var j = i * 4;
            pixelData[i] = (byte)((pixels[j] * 38 + pixels[j + 1] * 75 + pixels[j + 2] * 15) >> 7);
        }
    }

    public async Task<bool> VerifyAsync(string hostName, int port, string calledAE)
    {
        try
        {
            if (!ValidateConnectionParameters(hostName, port, calledAE, "VerifyAsync"))
                return false;

            var client = CreateClient(hostName, port, _aeTitle, calledAE);
            var echo = new DicomCEchoRequest();
            await client.AddRequestAsync(echo);
            await client.SendAsync();
            return true;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "Error occurred while verifying connection");
            return false;
        }
    }

    public async Task<bool> PrintAsync(PrintRequest request)
    {
        try
        {
            SetDefaultValues(request);
            if (!ValidatePrintParameters(request))
                return false;

            DicomLogger.Information("PrintSCU", "Starting print job - From {CallingAE} to {CalledAE}@{Host}:{Port}",
                _aeTitle, request.CalledAE, request.HostName, request.Port);

            var file = await LoadDicomFileAsync(request.FilePath);
            var client = CreateClient(request.HostName, request.Port, _aeTitle, request.CalledAE);

            // 1. Create Film Session
            var filmSessionRequest = new DicomNCreateRequest(DicomUID.BasicFilmSession, DicomUID.Generate());
            filmSessionRequest.Dataset = CreateFilmSessionDataset(request);

            DicomResponse? filmSessionResponse = null;
            var filmSessionTcs = new TaskCompletionSource<bool>();

            filmSessionRequest.OnResponseReceived = (req, res) =>
            {
                filmSessionResponse = res;
                if (res.Status.State != DicomState.Success)
                {
                    DicomLogger.Error("PrintSCU", "Failed to create Film Session: {Status}", res.Status);
                    filmSessionTcs.SetResult(false);
                    return;
                }

                var filmSessionUid = res.Command.GetString(DicomTag.AffectedSOPInstanceUID);
                DicomLogger.Information("PrintSCU", "Film Session created successfully, UID: {Uid}", filmSessionUid);

                // 2. Create Film Box
                var filmBoxRequest = new DicomNCreateRequest(DicomUID.BasicFilmBox, DicomUID.Generate());
                filmBoxRequest.Dataset = CreateFilmBoxDataset(request);
                filmBoxRequest.Dataset.Add(DicomTag.ReferencedFilmSessionSequence, new DicomDataset[]
                {
                new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomUID.BasicFilmSession },
                    { DicomTag.ReferencedSOPInstanceUID, filmSessionUid }
                }
                });

                filmBoxRequest.OnResponseReceived = (fbReq, fbRes) =>
                {
                    if (fbRes.Status.State != DicomState.Success)
                    {
                        DicomLogger.Error("PrintSCU", "Failed to create Film Box: {Status}", fbRes.Status);
                        filmSessionTcs.SetResult(false);
                        return;
                    }

                    var imageBoxSequence = fbRes.Dataset?.GetSequence(DicomTag.ReferencedImageBoxSequence);
                    if (imageBoxSequence == null || !imageBoxSequence.Items.Any())
                    {
                        DicomLogger.Error("PrintSCU", "No Image Box reference found");
                        filmSessionTcs.SetResult(false);
                        return;
                    }

                    var imageBoxItem = imageBoxSequence.Items[0];
                    var imageBoxClassUid = imageBoxItem.GetSingleValue<DicomUID>(DicomTag.ReferencedSOPClassUID);
                    var imageBoxInstanceUid = imageBoxItem.GetSingleValue<DicomUID>(DicomTag.ReferencedSOPInstanceUID);

                    // 3. Set Image Box
                    var imageBoxRequest = new DicomNSetRequest(imageBoxClassUid, imageBoxInstanceUid);
                    imageBoxRequest.Dataset = CreateImageBoxDataset(file);

                    imageBoxRequest.OnResponseReceived = (ibReq, ibRes) =>
                    {
                        if (ibRes.Status.State != DicomState.Success)
                        {
                            DicomLogger.Error("PrintSCU", "Failed to set Image Box: {Status}", ibRes.Status);
                            filmSessionTcs.SetResult(false);
                            return;
                        }

                        // 4. Execute print
                        var printRequest = new DicomNActionRequest(DicomUID.BasicFilmSession, DicomUID.Parse(filmSessionUid), 1);
                        printRequest.OnResponseReceived = (pReq, pRes) =>
                        {
                            if (pRes.Status.State != DicomState.Success)
                            {
                                DicomLogger.Error("PrintSCU", "Print operation failed: {Status}", pRes.Status);
                                filmSessionTcs.SetResult(false);
                                return;
                            }
                            filmSessionTcs.SetResult(true);
                        };

                        client.AddRequestAsync(printRequest).Wait();
                        client.SendAsync().Wait();
                    };

                    client.AddRequestAsync(imageBoxRequest).Wait();
                    client.SendAsync().Wait();
                };

                client.AddRequestAsync(filmBoxRequest).Wait();
                client.SendAsync().Wait();
            };

            await client.AddRequestAsync(filmSessionRequest);
            await client.SendAsync();

            var result = await filmSessionTcs.Task;
            if (result)
            {
                DicomLogger.Information("PrintSCU", "Print job completed");
            }
            return result;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("PrintSCU", ex, "Error occurred during printing");
            return false;
        }
    }
