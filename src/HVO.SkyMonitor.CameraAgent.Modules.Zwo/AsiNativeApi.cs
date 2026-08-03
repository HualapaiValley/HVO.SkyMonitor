using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Modules.Zwo;

internal enum AsiErrorCode
{
    Success = 0,
    InvalidIndex = 1,
    InvalidId = 2,
    InvalidControlType = 3,
    CameraClosed = 4,
    CameraRemoved = 5,
    InvalidPath = 6,
    InvalidFileFormat = 7,
    InvalidSize = 8,
    InvalidImageType = 9,
    OutOfBoundary = 10,
    Timeout = 11,
    InvalidSequence = 12,
    BufferTooSmall = 13,
    VideoModeActive = 14,
    ExposureInProgress = 15,
    GeneralError = 16,
    InvalidMode = 17,
    GpsNotSupported = 18,
    GpsVersionError = 19,
    GpsFpgaError = 20,
    GpsParameterOutOfRange = 21,
    GpsDataInvalid = 22,
    End = 23
}

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "The internal exception is created only from a native ASI operation and error code.")]
[SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "The typed ASI error is an internal native-adapter contract.")]
internal sealed class AsiException : Exception
{
    internal AsiException(string operation, AsiErrorCode errorCode)
        : base($"{operation} failed with ASI error {(int)errorCode} ({errorCode}).")
    {
        ErrorCode = errorCode;
    }

    internal AsiErrorCode ErrorCode { get; }
}

internal enum AsiBool
{
    False = 0,
    True = 1
}

internal enum AsiBayerPattern
{
    Rg = 0,
    Bg = 1,
    Gr = 2,
    Gb = 3
}

internal enum AsiImageType
{
    End = -1,
    Raw8 = 0,
    Rgb24 = 1,
    Raw16 = 2,
    Y8 = 3
}

internal enum AsiControlType
{
    Gain = 0,
    Exposure = 1,
    Gamma = 2,
    WhiteBalanceRed = 3,
    WhiteBalanceBlue = 4,
    Offset = 5,
    BandwidthOverload = 6,
    Overclock = 7,
    Temperature = 8,
    Flip = 9,
    AutoMaxGain = 10,
    AutoMaxExposure = 11,
    AutoTargetBrightness = 12,
    HardwareBin = 13,
    HighSpeedMode = 14,
    CoolerPowerPercent = 15,
    TargetTemperature = 16,
    CoolerOn = 17,
    MonoBin = 18,
    FanOn = 19,
    PatternAdjust = 20,
    AntiDewHeater = 21,
    FanAdjust = 22,
    PowerLedBrightness = 23,
    UsbHubReset = 24,
    GpsSupport = 25,
    GpsStartLine = 26,
    GpsEndLine = 27,
    RollingInterval = 28
}

internal enum AsiExposureStatus
{
    Idle = 0,
    Working = 1,
    Success = 2,
    Failed = 3
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AsiCameraInfoNative
{
    internal fixed byte Name[64];
    internal int CameraId;
    internal CLong MaxHeight;
    internal CLong MaxWidth;
    internal AsiBool IsColorCamera;
    internal AsiBayerPattern BayerPattern;
    internal fixed int SupportedBins[16];
    internal fixed int SupportedVideoFormats[8];
    internal double PixelSize;
    internal AsiBool MechanicalShutter;
    internal AsiBool St4Port;
    internal AsiBool IsCoolerCamera;
    internal AsiBool IsUsb3Host;
    internal AsiBool IsUsb3Camera;
    internal float ElectronsPerAdu;
    internal int BitDepth;
    internal AsiBool IsTriggerCamera;
    internal fixed byte Unused[16];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AsiControlCapsNative
{
    internal fixed byte Name[64];
    internal fixed byte Description[128];
    internal CLong Maximum;
    internal CLong Minimum;
    internal CLong Default;
    internal AsiBool IsAutoSupported;
    internal AsiBool IsWritable;
    internal AsiControlType ControlType;
    internal fixed byte Unused[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AsiSerialNumberNative
{
    internal fixed byte Id[8];
}

internal static class AsiAbi
{
    internal static void Validate()
    {
        AssertSize<AsiCameraInfoNative>(248, "ASI_CAMERA_INFO");
        AssertSize<AsiControlCapsNative>(264, "ASI_CONTROL_CAPS");
        AssertSize<AsiSerialNumberNative>(8, "ASI_SN");
        AssertOffset<AsiCameraInfoNative>(nameof(AsiCameraInfoNative.CameraId), 64);
        AssertOffset<AsiCameraInfoNative>(nameof(AsiCameraInfoNative.MaxHeight), 72);
        AssertOffset<AsiCameraInfoNative>(nameof(AsiCameraInfoNative.MaxWidth), 80);
        AssertOffset<AsiCameraInfoNative>(nameof(AsiCameraInfoNative.PixelSize), 192);
        AssertOffset<AsiCameraInfoNative>(nameof(AsiCameraInfoNative.BitDepth), 224);
        AssertOffset<AsiControlCapsNative>(nameof(AsiControlCapsNative.Maximum), 192);
        AssertOffset<AsiControlCapsNative>(nameof(AsiControlCapsNative.ControlType), 224);
    }

    private static void AssertSize<T>(int expected, string nativeName) where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expected)
        {
            throw new PlatformNotSupportedException($"{nativeName} ABI size is {actual}; Linux LP64 requires {expected}.");
        }
    }

    private static void AssertOffset<T>(string field, int expected) where T : struct
    {
        var actual = Marshal.OffsetOf<T>(field).ToInt32();
        if (actual != expected)
        {
            throw new PlatformNotSupportedException($"{typeof(T).Name}.{field} ABI offset is {actual}; expected {expected}.");
        }
    }
}

internal sealed record AsiCameraInfo(
    int CameraId,
    string Model,
    long MaximumWidth,
    long MaximumHeight,
    bool IsColorCamera,
    AsiBayerPattern BayerPattern,
    IReadOnlyList<int> SupportedBins,
    IReadOnlyList<AsiImageType> SupportedImageTypes,
    double PixelSizeMicrons,
    int BitDepth);

internal sealed record AsiControlCaps(
    AsiControlType Type,
    long Minimum,
    long Maximum,
    long Default,
    bool IsAutoSupported,
    bool IsWritable);

internal interface IAsiNativeApi : IDisposable
{
    string GetSdkVersion();
    int GetCameraCount();
    AsiCameraInfo GetCameraProperty(int cameraIndex);
    byte[] GetSerialNumber(int cameraId);
    void OpenCamera(int cameraId);
    void InitializeCamera(int cameraId);
    void CloseCamera(int cameraId);
    IReadOnlyList<AsiControlCaps> GetControlCapabilities(int cameraId);
    (long Value, bool Automatic) GetControlValue(int cameraId, AsiControlType type);
    void SetControlValue(int cameraId, AsiControlType type, long value, bool automatic);
    void SetRoiFormat(int cameraId, int width, int height, int bin, AsiImageType imageType);
    (int Width, int Height, int Bin, AsiImageType ImageType) GetRoiFormat(int cameraId);
    void SetStartPosition(int cameraId, int x, int y);
    (int X, int Y) GetStartPosition(int cameraId);
    void StartExposure(int cameraId, bool dark);
    AsiExposureStatus GetExposureStatus(int cameraId);
    void StopExposure(int cameraId);
    void GetDataAfterExposure(int cameraId, IntPtr buffer, long bufferSize);
}

internal interface IAsiLibraryLoader
{
    IntPtr Load(string path);
    IntPtr GetExport(IntPtr handle, string name);
    void Free(IntPtr handle);
}

internal sealed class SystemAsiLibraryLoader : IAsiLibraryLoader
{
    public IntPtr Load(string path) => NativeLibrary.Load(path);
    public IntPtr GetExport(IntPtr handle, string name) => NativeLibrary.GetExport(handle, name);
    public void Free(IntPtr handle) => NativeLibrary.Free(handle);
}

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Library failures must be sanitized and cleanup must not replace the load failure.")]
internal sealed unsafe class AsiNativeApi : IAsiNativeApi
{
    private static readonly string[] RequiredExports =
    [
        "ASIGetSDKVersion", "ASIGetNumOfConnectedCameras", "ASIGetCameraProperty", "ASIGetSerialNumber",
        "ASIOpenCamera", "ASIInitCamera", "ASICloseCamera", "ASIGetNumOfControls", "ASIGetControlCaps",
        "ASIGetControlValue", "ASISetControlValue", "ASISetROIFormat", "ASIGetROIFormat", "ASISetStartPos",
        "ASIGetStartPos", "ASIStartExposure", "ASIGetExpStatus", "ASIStopExposure", "ASIGetDataAfterExp"
    ];

    private readonly IAsiLibraryLoader _loader;
    private IntPtr _handle;
    private readonly GetSdkVersionDelegate _getSdkVersion;
    private readonly GetCameraCountDelegate _getCameraCount;
    private readonly GetCameraPropertyDelegate _getCameraProperty;
    private readonly GetSerialNumberDelegate _getSerialNumber;
    private readonly CameraCallDelegate _openCamera;
    private readonly CameraCallDelegate _initializeCamera;
    private readonly CameraCallDelegate _closeCamera;
    private readonly GetNumberOfControlsDelegate _getNumberOfControls;
    private readonly GetControlCapsDelegate _getControlCaps;
    private readonly GetControlValueDelegate _getControlValue;
    private readonly SetControlValueDelegate _setControlValue;
    private readonly SetRoiFormatDelegate _setRoiFormat;
    private readonly GetRoiFormatDelegate _getRoiFormat;
    private readonly SetStartPositionDelegate _setStartPosition;
    private readonly GetStartPositionDelegate _getStartPosition;
    private readonly StartExposureDelegate _startExposure;
    private readonly GetExposureStatusDelegate _getExposureStatus;
    private readonly CameraCallDelegate _stopExposure;
    private readonly GetDataAfterExposureDelegate _getDataAfterExposure;

    private AsiNativeApi(IntPtr handle, IAsiLibraryLoader loader, IReadOnlyDictionary<string, IntPtr> exports)
    {
        _handle = handle;
        _loader = loader;
        _getSdkVersion = Bind<GetSdkVersionDelegate>(exports, "ASIGetSDKVersion");
        _getCameraCount = Bind<GetCameraCountDelegate>(exports, "ASIGetNumOfConnectedCameras");
        _getCameraProperty = Bind<GetCameraPropertyDelegate>(exports, "ASIGetCameraProperty");
        _getSerialNumber = Bind<GetSerialNumberDelegate>(exports, "ASIGetSerialNumber");
        _openCamera = Bind<CameraCallDelegate>(exports, "ASIOpenCamera");
        _initializeCamera = Bind<CameraCallDelegate>(exports, "ASIInitCamera");
        _closeCamera = Bind<CameraCallDelegate>(exports, "ASICloseCamera");
        _getNumberOfControls = Bind<GetNumberOfControlsDelegate>(exports, "ASIGetNumOfControls");
        _getControlCaps = Bind<GetControlCapsDelegate>(exports, "ASIGetControlCaps");
        _getControlValue = Bind<GetControlValueDelegate>(exports, "ASIGetControlValue");
        _setControlValue = Bind<SetControlValueDelegate>(exports, "ASISetControlValue");
        _setRoiFormat = Bind<SetRoiFormatDelegate>(exports, "ASISetROIFormat");
        _getRoiFormat = Bind<GetRoiFormatDelegate>(exports, "ASIGetROIFormat");
        _setStartPosition = Bind<SetStartPositionDelegate>(exports, "ASISetStartPos");
        _getStartPosition = Bind<GetStartPositionDelegate>(exports, "ASIGetStartPos");
        _startExposure = Bind<StartExposureDelegate>(exports, "ASIStartExposure");
        _getExposureStatus = Bind<GetExposureStatusDelegate>(exports, "ASIGetExpStatus");
        _stopExposure = Bind<CameraCallDelegate>(exports, "ASIStopExposure");
        _getDataAfterExposure = Bind<GetDataAfterExposureDelegate>(exports, "ASIGetDataAfterExp");
    }

    internal static AsiNativeApi Load(string path, IAsiLibraryLoader? loader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AsiAbi.Validate();
        loader ??= new SystemAsiLibraryLoader();
        IntPtr handle;
        try
        {
            handle = loader.Load(path);
        }
        catch
        {
            throw new InvalidOperationException("The configured ASI SDK library could not be loaded or bound.");
        }
        try
        {
            var exports = RequiredExports.ToDictionary(name => name, name => loader.GetExport(handle, name), StringComparer.Ordinal);
            return new AsiNativeApi(handle, loader, exports);
        }
        catch
        {
            try
            {
                loader.Free(handle);
            }
            catch
            {
                // Preserve the sanitized load/bind failure.
            }
            throw new InvalidOperationException("The configured ASI SDK library could not be loaded or bound.");
        }
    }

    public string GetSdkVersion()
        => Marshal.PtrToStringAnsi(_getSdkVersion())
            ?? throw new InvalidOperationException("The ASI SDK returned an empty version string.");

    public int GetCameraCount() => _getCameraCount();

    public AsiCameraInfo GetCameraProperty(int cameraIndex)
    {
        var native = new AsiCameraInfoNative();
        Check(_getCameraProperty(ref native, cameraIndex), "ASIGetCameraProperty");
        var bins = new List<int>(16);
        var formats = new List<AsiImageType>(8);
        byte* name = native.Name;
        int* supportedBins = native.SupportedBins;
        int* supportedFormats = native.SupportedVideoFormats;
        for (var index = 0; index < 16 && supportedBins[index] != 0; index++)
        {
            bins.Add(supportedBins[index]);
        }
        for (var index = 0; index < 8 && supportedFormats[index] != (int)AsiImageType.End; index++)
        {
            formats.Add((AsiImageType)supportedFormats[index]);
        }
        return new AsiCameraInfo(
            native.CameraId,
            ReadAnsi(name, 64),
            native.MaxWidth.Value,
            native.MaxHeight.Value,
            native.IsColorCamera == AsiBool.True,
            native.BayerPattern,
            bins,
            formats,
            native.PixelSize,
            native.BitDepth);
    }

    public byte[] GetSerialNumber(int cameraId)
    {
        var serial = new AsiSerialNumberNative();
        Check(_getSerialNumber(cameraId, ref serial), "ASIGetSerialNumber");
        var result = new byte[8];
        byte* source = serial.Id;
        Marshal.Copy((IntPtr)source, result, 0, result.Length);
        return result;
    }

    public void OpenCamera(int cameraId) => Check(_openCamera(cameraId), "ASIOpenCamera");
    public void InitializeCamera(int cameraId) => Check(_initializeCamera(cameraId), "ASIInitCamera");
    public void CloseCamera(int cameraId) => Check(_closeCamera(cameraId), "ASICloseCamera");

    public IReadOnlyList<AsiControlCaps> GetControlCapabilities(int cameraId)
    {
        Check(_getNumberOfControls(cameraId, out var count), "ASIGetNumOfControls");
        if (count is < 0 or > 256)
        {
            throw new InvalidOperationException("The ASI SDK returned an invalid control count.");
        }
        var result = new List<AsiControlCaps>(count);
        for (var index = 0; index < count; index++)
        {
            var native = new AsiControlCapsNative();
            Check(_getControlCaps(cameraId, index, ref native), "ASIGetControlCaps");
            result.Add(new AsiControlCaps(
                native.ControlType,
                native.Minimum.Value,
                native.Maximum.Value,
                native.Default.Value,
                native.IsAutoSupported == AsiBool.True,
                native.IsWritable == AsiBool.True));
        }
        return result;
    }

    public (long Value, bool Automatic) GetControlValue(int cameraId, AsiControlType type)
    {
        Check(_getControlValue(cameraId, type, out var value, out var automatic), "ASIGetControlValue");
        return (value.Value, automatic == AsiBool.True);
    }

    public void SetControlValue(int cameraId, AsiControlType type, long value, bool automatic)
        => Check(_setControlValue(cameraId, type, new CLong((nint)value), automatic ? AsiBool.True : AsiBool.False), "ASISetControlValue");

    public void SetRoiFormat(int cameraId, int width, int height, int bin, AsiImageType imageType)
        => Check(_setRoiFormat(cameraId, width, height, bin, imageType), "ASISetROIFormat");

    public (int Width, int Height, int Bin, AsiImageType ImageType) GetRoiFormat(int cameraId)
    {
        Check(_getRoiFormat(cameraId, out var width, out var height, out var bin, out var imageType), "ASIGetROIFormat");
        return (width, height, bin, imageType);
    }

    public void SetStartPosition(int cameraId, int x, int y)
        => Check(_setStartPosition(cameraId, x, y), "ASISetStartPos");

    public (int X, int Y) GetStartPosition(int cameraId)
    {
        Check(_getStartPosition(cameraId, out var x, out var y), "ASIGetStartPos");
        return (x, y);
    }

    public void StartExposure(int cameraId, bool dark)
        => Check(_startExposure(cameraId, dark ? AsiBool.True : AsiBool.False), "ASIStartExposure");

    public AsiExposureStatus GetExposureStatus(int cameraId)
    {
        Check(_getExposureStatus(cameraId, out var status), "ASIGetExpStatus");
        return status;
    }

    public void StopExposure(int cameraId) => Check(_stopExposure(cameraId), "ASIStopExposure");

    public void GetDataAfterExposure(int cameraId, IntPtr buffer, long bufferSize)
        => Check(_getDataAfterExposure(cameraId, buffer, new CLong((nint)bufferSize)), "ASIGetDataAfterExp");

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _loader.Free(handle);
        }
    }

    private static T Bind<T>(IReadOnlyDictionary<string, IntPtr> exports, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(exports[name]);

    private static void Check(AsiErrorCode result, string operation)
    {
        if (result != AsiErrorCode.Success)
        {
            throw new AsiException(operation, result);
        }
    }

    private static string ReadAnsi(byte* value, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && value[length] != 0)
        {
            length++;
        }
        return Encoding.ASCII.GetString(value, length);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetSdkVersionDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCameraCountDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetCameraPropertyDelegate(ref AsiCameraInfoNative info, int cameraIndex);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetSerialNumberDelegate(int cameraId, ref AsiSerialNumberNative serial);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode CameraCallDelegate(int cameraId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetNumberOfControlsDelegate(int cameraId, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetControlCapsDelegate(int cameraId, int controlIndex, ref AsiControlCapsNative caps);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetControlValueDelegate(int cameraId, AsiControlType type, out CLong value, out AsiBool automatic);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode SetControlValueDelegate(int cameraId, AsiControlType type, CLong value, AsiBool automatic);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode SetRoiFormatDelegate(int cameraId, int width, int height, int bin, AsiImageType imageType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetRoiFormatDelegate(int cameraId, out int width, out int height, out int bin, out AsiImageType imageType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode SetStartPositionDelegate(int cameraId, int x, int y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetStartPositionDelegate(int cameraId, out int x, out int y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode StartExposureDelegate(int cameraId, AsiBool dark);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetExposureStatusDelegate(int cameraId, out AsiExposureStatus status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate AsiErrorCode GetDataAfterExposureDelegate(int cameraId, IntPtr buffer, CLong bufferSize);
}
