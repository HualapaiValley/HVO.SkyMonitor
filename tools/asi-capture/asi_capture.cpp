#include "ASICamera2.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <climits>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <map>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace
{
struct Options
{
    std::filesystem::path outputDirectory = "captures";
    long exposureMicroseconds = 1'000'000;
    long gain = 0;
    long offset = 10;
    int count = 1;
    int cameraIndex = 0;
    int bin = 1;
    bool monoBin = false;
    bool hardwareBin = false;
    bool profileOnly = false;
};

struct CameraCloser
{
    int id = -1;
    ~CameraCloser()
    {
        if (id >= 0)
        {
            ASICloseCamera(id);
        }
    }
};

void Check(ASI_ERROR_CODE result, const std::string& operation)
{
    if (result != ASI_SUCCESS)
    {
        throw std::runtime_error(operation + " failed with ASI error " + std::to_string(result));
    }
}

long ParseLong(const char* value, const std::string& name)
{
    std::size_t consumed = 0;
    const std::string text(value);
    const auto result = std::stol(text, &consumed);
    if (consumed != text.size())
    {
        throw std::invalid_argument("Invalid " + name + ": " + text);
    }
    return result;
}

int ParseInt(const char* value, const std::string& name)
{
    const auto parsed = ParseLong(value, name);
    if (parsed < std::numeric_limits<int>::min() || parsed > std::numeric_limits<int>::max())
    {
        throw std::out_of_range(name + " is outside the supported integer range.");
    }
    return static_cast<int>(parsed);
}

Options ParseOptions(int argc, char** argv)
{
    Options options;
    for (int index = 1; index < argc; index++)
    {
        const std::string argument(argv[index]);
        if (argument == "--output-dir" && index + 1 < argc)
        {
            options.outputDirectory = argv[++index];
        }
        else if (argument == "--exposure-us" && index + 1 < argc)
        {
            options.exposureMicroseconds = ParseLong(argv[++index], "exposure");
        }
        else if (argument == "--gain" && index + 1 < argc)
        {
            options.gain = ParseLong(argv[++index], "gain");
        }
        else if (argument == "--offset" && index + 1 < argc)
        {
            options.offset = ParseLong(argv[++index], "offset");
        }
        else if (argument == "--count" && index + 1 < argc)
        {
            options.count = ParseInt(argv[++index], "count");
        }
        else if (argument == "--camera-index" && index + 1 < argc)
        {
            options.cameraIndex = ParseInt(argv[++index], "camera index");
        }
        else if (argument == "--bin" && index + 1 < argc)
        {
            options.bin = ParseInt(argv[++index], "bin");
        }
        else if (argument == "--mono-bin")
        {
            options.monoBin = true;
        }
        else if (argument == "--hardware-bin")
        {
            options.hardwareBin = true;
        }
        else if (argument == "--profile-only")
        {
            options.profileOnly = true;
        }
        else
        {
            throw std::invalid_argument(
                "Usage: asi-capture [--output-dir PATH] [--exposure-us N] [--gain N] "
                "[--offset N] [--count N] [--camera-index N] [--bin N] "
                "[--mono-bin] [--hardware-bin] [--profile-only]");
        }
    }

    if (options.exposureMicroseconds <= 0 || options.count <= 0 || options.count > 100 ||
        options.cameraIndex < 0 || options.bin <= 0)
    {
        throw std::invalid_argument("Exposure, count, camera index, or bin is outside the supported range.");
    }
    return options;
}

std::string BayerPatternName(ASI_BAYER_PATTERN pattern)
{
    switch (pattern)
    {
        case ASI_BAYER_RG: return "RGGB";
        case ASI_BAYER_BG: return "BGGR";
        case ASI_BAYER_GR: return "GRBG";
        case ASI_BAYER_GB: return "GBRG";
        default: return "Unknown(" + std::to_string(static_cast<int>(pattern)) + ")";
    }
}

std::string ImageTypeName(ASI_IMG_TYPE imageType)
{
    switch (imageType)
    {
        case ASI_IMG_RAW8: return "RAW8";
        case ASI_IMG_RGB24: return "RGB24";
        case ASI_IMG_RAW16: return "RAW16";
        case ASI_IMG_Y8: return "Y8";
        default: return "Unknown(" + std::to_string(static_cast<int>(imageType)) + ")";
    }
}

std::string SampleLayoutName(const ASI_CAMERA_INFO& info, long monoBin, long flip)
{
    if (monoBin == 1)
    {
        return "Mono16";
    }
    if (flip != ASI_FLIP_NONE)
    {
        return "Unspecified";
    }
    if (!info.IsColorCam)
    {
        return "Mono16";
    }
    switch (info.BayerPattern)
    {
        case ASI_BAYER_RG: return "RGGB16";
        case ASI_BAYER_BG: return "BGGR16";
        case ASI_BAYER_GR: return "GRBG16";
        case ASI_BAYER_GB: return "GBRG16";
        default: return "Unspecified";
    }
}

std::string JsonEscape(const std::string& value)
{
    std::ostringstream output;
    for (const auto character : value)
    {
        switch (character)
        {
            case '\\': output << "\\\\"; break;
            case '"': output << "\\\""; break;
            case '\n': output << "\\n"; break;
            case '\r': output << "\\r"; break;
            case '\t': output << "\\t"; break;
            default: output << character; break;
        }
    }
    return output.str();
}

std::string UtcTimestamp(bool fileSafe)
{
    const auto now = std::chrono::system_clock::now();
    const auto milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()) % 1000;
    const auto time = std::chrono::system_clock::to_time_t(now);
    std::tm utc{};
    gmtime_r(&time, &utc);
    std::ostringstream output;
    output << std::put_time(&utc, fileSafe ? "%Y%m%dT%H%M%S" : "%Y-%m-%dT%H:%M:%S")
           << (fileSafe ? "" : ".") << std::setfill('0') << std::setw(3) << milliseconds.count() << 'Z';
    return output.str();
}

bool SupportsRaw16(const ASI_CAMERA_INFO& info)
{
    for (const auto format : info.SupportedVideoFormat)
    {
        if (format == ASI_IMG_RAW16)
        {
            return true;
        }
        if (format == ASI_IMG_END)
        {
            break;
        }
    }
    return false;
}

bool SupportsBin(const ASI_CAMERA_INFO& info, int requestedBin)
{
    for (const auto supportedBin : info.SupportedBins)
    {
        if (supportedBin == requestedBin)
        {
            return true;
        }
        if (supportedBin == 0)
        {
            break;
        }
    }
    return false;
}

std::map<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS> ReadControlCaps(int cameraId)
{
    int count = 0;
    Check(ASIGetNumOfControls(cameraId, &count), "ASIGetNumOfControls");
    std::map<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS> controls;
    for (int index = 0; index < count; index++)
    {
        ASI_CONTROL_CAPS caps{};
        Check(ASIGetControlCaps(cameraId, index, &caps), "ASIGetControlCaps");
        controls[caps.ControlType] = caps;
    }
    return controls;
}

void SetControl(
    int cameraId,
    const std::map<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS>& controls,
    ASI_CONTROL_TYPE type,
    long value,
    const std::string& name)
{
    const auto iterator = controls.find(type);
    if (iterator == controls.end() || iterator->second.IsWritable == ASI_FALSE)
    {
        throw std::runtime_error(name + " is not writable on this camera.");
    }
    if (value < iterator->second.MinValue || value > iterator->second.MaxValue)
    {
        throw std::out_of_range(
            name + " must be between " + std::to_string(iterator->second.MinValue) + " and " +
            std::to_string(iterator->second.MaxValue));
    }
    Check(ASISetControlValue(cameraId, type, value, ASI_FALSE), "ASISetControlValue(" + name + ")");
}

long ReadControl(int cameraId, ASI_CONTROL_TYPE type)
{
    long value = 0;
    ASI_BOOL automatic = ASI_FALSE;
    Check(ASIGetControlValue(cameraId, type, &value, &automatic), "ASIGetControlValue");
    return value;
}

std::string SerialNumber(int cameraId)
{
    ASI_SN serial{};
    if (ASIGetSerialNumber(cameraId, &serial) != ASI_SUCCESS)
    {
        return {};
    }
    std::ostringstream output;
    output << std::hex << std::setfill('0');
    for (const auto value : serial.id)
    {
        output << std::setw(2) << static_cast<int>(value);
    }
    return output.str();
}

void PublishFile(
    std::ofstream& output,
    const std::filesystem::path& temporaryPath,
    const std::filesystem::path& finalPath)
{
    output.flush();
    if (!output)
    {
        throw std::runtime_error("Unable to flush " + temporaryPath.string());
    }
    output.close();
    if (output.fail())
    {
        throw std::runtime_error("Unable to close " + temporaryPath.string());
    }
    std::error_code error;
    std::filesystem::rename(temporaryPath, finalPath, error);
    if (error)
    {
        throw std::runtime_error("Unable to publish " + finalPath.string() + ": " + error.message());
    }
}

std::filesystem::path WriteProfile(
    const std::filesystem::path& outputDirectory,
    const ASI_CAMERA_INFO& info,
    const std::string& serial,
    const std::map<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS>& controls)
{
    const auto profilePath = outputDirectory /
        ("asi-profile-" + (serial.empty() ? std::to_string(info.CameraID) : serial) + "-" +
            UtcTimestamp(true) + ".json");
    auto temporaryPath = profilePath;
    temporaryPath += ".partial";
    std::ofstream output(temporaryPath, std::ios::trunc);
    if (!output)
    {
        throw std::runtime_error("Unable to create profile file " + temporaryPath.string());
    }
    output << std::setprecision(17)
           << "{\n"
           << "  \"schemaVersion\": \"hvo-asi-sdk-profile-v1\",\n"
           << "  \"evidenceType\": \"sdk-reported\",\n"
           << "  \"probedUtc\": \"" << UtcTimestamp(false) << "\",\n"
           << "  \"sdkVersion\": \"" << JsonEscape(ASIGetSDKVersion()) << "\",\n"
           << "  \"cameraModel\": \"" << JsonEscape(info.Name) << "\",\n"
           << "  \"cameraSerial\": \"" << JsonEscape(serial) << "\",\n"
           << "  \"cameraId\": " << info.CameraID << ",\n"
           << "  \"maximumWidth\": " << info.MaxWidth << ",\n"
           << "  \"maximumHeight\": " << info.MaxHeight << ",\n"
           << "  \"pixelSizeMicrons\": " << info.PixelSize << ",\n"
           << "  \"bitDepth\": " << info.BitDepth << ",\n"
           << "  \"electronsPerAduReported\": " << info.ElecPerADU << ",\n"
           << "  \"isColorCamera\": " << (info.IsColorCam ? "true" : "false") << ",\n"
           << "  \"bayerPatternCode\": " << static_cast<int>(info.BayerPattern) << ",\n"
           << "  \"bayerPattern\": \"" << BayerPatternName(info.BayerPattern) << "\",\n"
           << "  \"mechanicalShutter\": " << (info.MechanicalShutter ? "true" : "false") << ",\n"
           << "  \"st4Port\": " << (info.ST4Port ? "true" : "false") << ",\n"
           << "  \"isCoolerCamera\": " << (info.IsCoolerCam ? "true" : "false") << ",\n"
           << "  \"isUsb3Camera\": " << (info.IsUSB3Camera ? "true" : "false") << ",\n"
           << "  \"isUsb3Host\": " << (info.IsUSB3Host ? "true" : "false") << ",\n"
           << "  \"isTriggerCamera\": " << (info.IsTriggerCam ? "true" : "false") << ",\n"
           << "  \"supportedBins\": [";
    bool first = true;
    for (const auto bin : info.SupportedBins)
    {
        if (bin == 0)
        {
            break;
        }
        output << (first ? "" : ", ") << bin;
        first = false;
    }
    output << "],\n  \"supportedPixelFormats\": [";
    first = true;
    for (const auto imageType : info.SupportedVideoFormat)
    {
        if (imageType == ASI_IMG_END)
        {
            break;
        }
        output << (first ? "" : ", ")
               << "{\"code\": " << static_cast<int>(imageType)
               << ", \"name\": \"" << ImageTypeName(imageType) << "\"}";
        first = false;
    }
    output << "],\n  \"controls\": [\n";
    first = true;
    for (const auto& [type, caps] : controls)
    {
        if (!first)
        {
            output << ",\n";
        }
        output << "    {\"type\": " << static_cast<int>(type)
               << ", \"name\": \"" << JsonEscape(caps.Name)
               << "\", \"description\": \"" << JsonEscape(caps.Description)
               << "\", \"minimum\": " << caps.MinValue
               << ", \"maximum\": " << caps.MaxValue
               << ", \"default\": " << caps.DefaultValue
               << ", \"autoSupported\": " << (caps.IsAutoSupported ? "true" : "false")
               << ", \"writable\": " << (caps.IsWritable ? "true" : "false") << "}";
        first = false;
    }
    output << "\n  ]\n}\n";
    try
    {
        PublishFile(output, temporaryPath, profilePath);
    }
    catch (...)
    {
        output.close();
        std::error_code ignored;
        std::filesystem::remove(temporaryPath, ignored);
        throw;
    }
    return profilePath;
}

struct Statistics
{
    std::uint16_t minimum;
    std::uint16_t maximum;
    double mean;
    std::uint16_t p01;
    std::uint16_t p50;
    std::uint16_t p99;
    std::uint64_t zeroCount;
    std::uint64_t nearContainerMaximumCount;
    std::array<double, 4> parityMeans;
};

Statistics CalculateStatistics(const std::vector<unsigned char>& bytes, int width)
{
    if (width <= 0 || bytes.empty() || bytes.size() % 2U != 0)
    {
        throw std::invalid_argument("RAW16 statistics require a positive width and a non-empty, even byte count.");
    }
    const auto sampleCount = bytes.size() / 2;
    if (sampleCount % static_cast<std::size_t>(width) != 0 || width < 2 ||
        sampleCount / static_cast<std::size_t>(width) < 2U)
    {
        throw std::invalid_argument("RAW16 statistics require complete rows and all four sample parities.");
    }
    std::vector<std::uint64_t> histogram(1U << 16U);
    long double total = 0;
    std::uint16_t minimum = UINT16_MAX;
    std::uint16_t maximum = 0;
    std::array<long double, 4> parityTotals{};
    std::array<std::uint64_t, 4> parityCounts{};
    for (std::size_t index = 0; index < sampleCount; index++)
    {
        const auto sample = static_cast<std::uint16_t>(
            bytes[index * 2] | static_cast<std::uint16_t>(bytes[index * 2 + 1]) << 8U);
        histogram[sample]++;
        total += sample;
        minimum = std::min(minimum, sample);
        maximum = std::max(maximum, sample);
        const auto row = index / static_cast<std::size_t>(width);
        const auto column = index % static_cast<std::size_t>(width);
        const auto parity = (row % 2U) * 2U + column % 2U;
        parityTotals[parity] += sample;
        parityCounts[parity]++;
    }

    const auto percentile = [&](double fraction)
    {
        const auto target = static_cast<std::uint64_t>(std::ceil(sampleCount * fraction));
        std::uint64_t cumulative = 0;
        for (std::size_t value = 0; value < histogram.size(); value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return static_cast<std::uint16_t>(value);
            }
        }
        return static_cast<std::uint16_t>(UINT16_MAX);
    };

    const auto nearContainerMaximumCount =
        histogram[UINT16_MAX] + histogram[UINT16_MAX - 1] +
        histogram[UINT16_MAX - 2] + histogram[UINT16_MAX - 3];
    return Statistics{
        minimum,
        maximum,
        static_cast<double>(total / static_cast<long double>(sampleCount)),
        percentile(0.01),
        percentile(0.50),
        percentile(0.99),
        histogram[0],
        nearContainerMaximumCount,
        {
            static_cast<double>(parityTotals[0] / static_cast<long double>(parityCounts[0])),
            static_cast<double>(parityTotals[1] / static_cast<long double>(parityCounts[1])),
            static_cast<double>(parityTotals[2] / static_cast<long double>(parityCounts[2])),
            static_cast<double>(parityTotals[3] / static_cast<long double>(parityCounts[3]))
        }};
}

void WriteMetadata(
    const std::filesystem::path& path,
    const ASI_CAMERA_INFO& info,
    const std::string& serial,
    const Options& options,
    int width,
    int height,
    int bin,
    long actualExposure,
    long actualGain,
    long actualOffset,
    long temperature,
    bool temperatureAvailable,
    long bandwidth,
    long highSpeedMode,
    long monoBin,
    long hardwareBin,
    long flip,
    const std::string& startedUtc,
    const std::string& completedUtc,
    double exposureElapsedMilliseconds,
    double downloadElapsedMilliseconds,
    const std::string& profileFile,
    const Statistics& statistics,
    int sequence)
{
    auto temporaryPath = path;
    temporaryPath += ".partial";
    std::ofstream output(temporaryPath, std::ios::trunc);
    if (!output)
    {
        throw std::runtime_error("Unable to create metadata file " + temporaryPath.string());
    }
    output << std::setprecision(17)
           << "{\n"
           << "  \"schemaVersion\": \"hvo-asi-raw16-sidecar-v2\",\n"
           << "  \"cameraModel\": \"" << JsonEscape(info.Name) << "\",\n"
           << "  \"cameraSerial\": \"" << JsonEscape(serial) << "\",\n"
           << "  \"sdkVersion\": \"" << JsonEscape(ASIGetSDKVersion()) << "\",\n"
           << "  \"sdkProfileFile\": \"" << JsonEscape(profileFile) << "\",\n"
           << "  \"pixelFormat\": \"RAW16\",\n"
           << "  \"sampleLayout\": \"" << SampleLayoutName(info, monoBin, flip) << "\",\n"
           << "  \"width\": " << width << ",\n"
           << "  \"height\": " << height << ",\n"
           << "  \"requestedBin\": " << options.bin << ",\n"
           << "  \"bin\": " << bin << ",\n"
           << "  \"requestedMonoBin\": " << (options.monoBin ? "true" : "false") << ",\n"
           << "  \"monoBin\": " << monoBin << ",\n"
           << "  \"requestedHardwareBin\": " << (options.hardwareBin ? "true" : "false") << ",\n"
           << "  \"hardwareBin\": " << hardwareBin << ",\n"
           << "  \"bitDepth\": " << info.BitDepth << ",\n"
           << "  \"pixelSizeMicrons\": " << info.PixelSize << ",\n"
           << "  \"electronsPerAduReported\": " << info.ElecPerADU << ",\n"
           << "  \"isColorCamera\": " << (info.IsColorCam ? "true" : "false") << ",\n"
           << "  \"bayerPattern\": " << static_cast<int>(info.BayerPattern) << ",\n"
           << "  \"bayerPatternName\": \"" << BayerPatternName(info.BayerPattern) << "\",\n"
           << "  \"isUsb3Camera\": " << (info.IsUSB3Camera ? "true" : "false") << ",\n"
           << "  \"isUsb3Host\": " << (info.IsUSB3Host ? "true" : "false") << ",\n"
           << "  \"requestedExposureMicroseconds\": " << options.exposureMicroseconds << ",\n"
           << "  \"exposureMicroseconds\": " << actualExposure << ",\n"
           << "  \"requestedGain\": " << options.gain << ",\n"
           << "  \"gain\": " << actualGain << ",\n"
           << "  \"requestedOffset\": " << options.offset << ",\n"
           << "  \"offset\": " << actualOffset << ",\n"
           << "  \"bandwidthOverload\": " << bandwidth << ",\n"
           << "  \"highSpeedMode\": " << highSpeedMode << ",\n"
           << "  \"requestedFlip\": 0,\n"
           << "  \"flip\": " << flip << ",\n"
           << "  \"rowStrideBytes\": " << static_cast<long long>(width) * 2 << ",\n"
           << "  \"byteCount\": " << static_cast<long long>(width) * height * 2 << ",\n"
           << "  \"sensorTemperatureC\": ";
    if (temperatureAvailable)
    {
        output << temperature / 10.0;
    }
    else
    {
        output << "null";
    }
    output << ",\n"
           << "  \"startedUtc\": \"" << startedUtc << "\",\n"
           << "  \"completedUtc\": \"" << completedUtc << "\",\n"
           << "  \"exposureElapsedMilliseconds\": " << exposureElapsedMilliseconds << ",\n"
           << "  \"downloadElapsedMilliseconds\": " << downloadElapsedMilliseconds << ",\n"
           << "  \"totalElapsedMilliseconds\": "
           << exposureElapsedMilliseconds + downloadElapsedMilliseconds << ",\n"
           << "  \"sequence\": " << sequence << ",\n"
           << "  \"statistics\": {\n"
           << "    \"minimumAdu\": " << statistics.minimum << ",\n"
           << "    \"maximumAdu\": " << statistics.maximum << ",\n"
           << "    \"meanAdu\": " << statistics.mean << ",\n"
           << "    \"p01Adu\": " << statistics.p01 << ",\n"
           << "    \"p50Adu\": " << statistics.p50 << ",\n"
           << "    \"p99Adu\": " << statistics.p99 << ",\n"
           << "    \"parityMeansAdu\": {\n"
           << "      \"rowEvenColumnEven\": " << statistics.parityMeans[0] << ",\n"
           << "      \"rowEvenColumnOdd\": " << statistics.parityMeans[1] << ",\n"
           << "      \"rowOddColumnEven\": " << statistics.parityMeans[2] << ",\n"
           << "      \"rowOddColumnOdd\": " << statistics.parityMeans[3] << "\n"
           << "    },\n"
           << "    \"zeroCount\": " << statistics.zeroCount << ",\n"
           << "    \"nearContainerMaximumCount\": " << statistics.nearContainerMaximumCount << "\n"
           << "  }\n"
           << "}\n";
    try
    {
        PublishFile(output, temporaryPath, path);
    }
    catch (...)
    {
        output.close();
        std::error_code ignored;
        std::filesystem::remove(temporaryPath, ignored);
        throw;
    }
}
}

int main(int argc, char** argv)
{
    try
    {
        const auto options = ParseOptions(argc, argv);
        const auto cameraCount = ASIGetNumOfConnectedCameras();
        if (cameraCount <= 0 || options.cameraIndex >= cameraCount)
        {
            throw std::runtime_error("Requested ASI camera is not connected.");
        }

        ASI_CAMERA_INFO info{};
        Check(ASIGetCameraProperty(&info, options.cameraIndex), "ASIGetCameraProperty");
        Check(ASIOpenCamera(info.CameraID), "ASIOpenCamera");
        CameraCloser closer{info.CameraID};
        Check(ASIInitCamera(info.CameraID), "ASIInitCamera");
        const auto controls = ReadControlCaps(info.CameraID);
        const auto serial = SerialNumber(info.CameraID);
        std::filesystem::create_directories(options.outputDirectory);
        const auto profilePath = WriteProfile(options.outputDirectory, info, serial, controls);
        std::cout << "SDK profile: " << profilePath << '\n';
        if (options.profileOnly)
        {
            return 0;
        }
        if (!SupportsRaw16(info))
        {
            throw std::runtime_error(std::string(info.Name) + " does not advertise RAW16 output.");
        }
        if (!SupportsBin(info, options.bin))
        {
            throw std::runtime_error(std::string(info.Name) + " does not advertise bin " +
                std::to_string(options.bin) + ".");
        }
        if (options.monoBin && options.bin == 1)
        {
            throw std::invalid_argument("--mono-bin requires --bin greater than 1.");
        }
        if (options.hardwareBin && options.bin != 2)
        {
            throw std::invalid_argument("--hardware-bin requires --bin 2.");
        }
        if (options.monoBin && options.hardwareBin)
        {
            throw std::invalid_argument("--mono-bin and --hardware-bin are separate experiments.");
        }

        SetControl(info.CameraID, controls, ASI_EXPOSURE, options.exposureMicroseconds, "exposure");
        SetControl(info.CameraID, controls, ASI_GAIN, options.gain, "gain");
        SetControl(info.CameraID, controls, ASI_OFFSET, options.offset, "offset");
        if (controls.contains(ASI_BANDWIDTHOVERLOAD) && controls.at(ASI_BANDWIDTHOVERLOAD).IsWritable)
        {
            SetControl(info.CameraID, controls, ASI_BANDWIDTHOVERLOAD, 40, "bandwidth overload");
        }
        if (controls.contains(ASI_HIGH_SPEED_MODE) && controls.at(ASI_HIGH_SPEED_MODE).IsWritable)
        {
            SetControl(info.CameraID, controls, ASI_HIGH_SPEED_MODE, 0, "high speed mode");
        }
        if (controls.contains(ASI_FLIP) && controls.at(ASI_FLIP).IsWritable)
        {
            SetControl(info.CameraID, controls, ASI_FLIP, ASI_FLIP_NONE, "flip");
        }
        if (controls.contains(ASI_MONO_BIN) && controls.at(ASI_MONO_BIN).IsWritable)
        {
            SetControl(info.CameraID, controls, ASI_MONO_BIN, options.monoBin ? 1 : 0, "mono bin");
        }
        else if (options.monoBin)
        {
            throw std::runtime_error("Mono-bin mode is not writable on this camera.");
        }
        const auto roiWidthLong = info.MaxWidth / options.bin;
        const auto roiHeightLong = info.MaxHeight / options.bin;
        if (roiWidthLong <= 0 || roiHeightLong <= 0 ||
            roiWidthLong > std::numeric_limits<int>::max() ||
            roiHeightLong > std::numeric_limits<int>::max())
        {
            throw std::overflow_error("The requested ROI dimensions are outside the SDK integer range.");
        }
        auto roiWidth = static_cast<int>(roiWidthLong);
        auto roiHeight = static_cast<int>(roiHeightLong);
        roiWidth -= roiWidth % 2;
        roiHeight -= roiHeight % 2;
        Check(ASISetROIFormat(
            info.CameraID,
            roiWidth,
            roiHeight,
            options.bin,
            ASI_IMG_RAW16), "ASISetROIFormat");
        Check(ASISetStartPos(info.CameraID, 0, 0), "ASISetStartPos");
        if (controls.contains(ASI_HARDWARE_BIN) && controls.at(ASI_HARDWARE_BIN).IsWritable)
        {
            SetControl(info.CameraID, controls, ASI_HARDWARE_BIN, options.hardwareBin ? 1 : 0, "hardware bin");
        }
        else if (options.hardwareBin)
        {
            throw std::runtime_error("Hardware-bin mode is not writable on this camera.");
        }

        int width = 0;
        int height = 0;
        int bin = 0;
        ASI_IMG_TYPE imageType = ASI_IMG_END;
        Check(ASIGetROIFormat(info.CameraID, &width, &height, &bin, &imageType), "ASIGetROIFormat");
        if (imageType != ASI_IMG_RAW16)
        {
            throw std::runtime_error("Camera did not retain RAW16 mode.");
        }
        if (width <= 0 || height <= 0)
        {
            throw std::runtime_error("Camera returned an empty or negative ROI.");
        }
        const auto widthSize = static_cast<std::size_t>(width);
        const auto heightSize = static_cast<std::size_t>(height);
        if (heightSize > std::numeric_limits<std::size_t>::max() / widthSize)
        {
            throw std::overflow_error("RAW16 sample count exceeds the process size type.");
        }
        const auto sampleCount = widthSize * heightSize;
        if (sampleCount > static_cast<std::size_t>(std::numeric_limits<long>::max()) / 2U)
        {
            throw std::overflow_error("RAW16 buffer exceeds the SDK buffer-size type.");
        }
        const auto byteCount = sampleCount * 2U;
        std::vector<unsigned char> pixels(byteCount);

        const auto actualExposure = ReadControl(info.CameraID, ASI_EXPOSURE);
        const auto actualGain = ReadControl(info.CameraID, ASI_GAIN);
        const auto actualOffset = ReadControl(info.CameraID, ASI_OFFSET);
        const auto bandwidth = controls.contains(ASI_BANDWIDTHOVERLOAD)
            ? ReadControl(info.CameraID, ASI_BANDWIDTHOVERLOAD) : -1;
        const auto highSpeedMode = controls.contains(ASI_HIGH_SPEED_MODE)
            ? ReadControl(info.CameraID, ASI_HIGH_SPEED_MODE) : -1;
        const auto monoBin = controls.contains(ASI_MONO_BIN)
            ? ReadControl(info.CameraID, ASI_MONO_BIN) : -1;
        const auto hardwareBin = controls.contains(ASI_HARDWARE_BIN)
            ? ReadControl(info.CameraID, ASI_HARDWARE_BIN) : -1;
        const auto flip = controls.contains(ASI_FLIP)
            ? ReadControl(info.CameraID, ASI_FLIP) : -1;

        std::cout << "Camera: " << info.Name << " " << width << 'x' << height
                  << " RAW16, SDK " << ASIGetSDKVersion() << '\n';
        std::cout << "Supported bins:";
        for (const auto supportedBin : info.SupportedBins)
        {
            if (supportedBin == 0)
            {
                break;
            }
            std::cout << ' ' << supportedBin;
        }
        std::cout << "; mono-bin control=" << (controls.contains(ASI_MONO_BIN) ? "yes" : "no") << '\n';
        for (int sequence = 1; sequence <= options.count; sequence++)
        {
            const auto startedUtc = UtcTimestamp(false);
            const auto exposureStarted = std::chrono::steady_clock::now();
            Check(ASIStartExposure(info.CameraID, ASI_FALSE), "ASIStartExposure");
            const auto deadline = std::chrono::steady_clock::now() +
                std::chrono::microseconds(actualExposure) + std::chrono::seconds(10);
            ASI_EXPOSURE_STATUS status = ASI_EXP_WORKING;
            while (status == ASI_EXP_WORKING)
            {
                if (std::chrono::steady_clock::now() >= deadline)
                {
                    ASIStopExposure(info.CameraID);
                    throw std::runtime_error("Exposure timed out.");
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                Check(ASIGetExpStatus(info.CameraID, &status), "ASIGetExpStatus");
            }
            if (status != ASI_EXP_SUCCESS)
            {
                throw std::runtime_error("Exposure failed with status " + std::to_string(status));
            }
            const auto exposureCompleted = std::chrono::steady_clock::now();
            Check(ASIGetDataAfterExp(info.CameraID, pixels.data(), static_cast<long>(pixels.size())),
                "ASIGetDataAfterExp");
            const auto downloadCompleted = std::chrono::steady_clock::now();
            const auto completedUtc = UtcTimestamp(false);
            const auto fileTimestamp = UtcTimestamp(true);
            const auto stem = fileTimestamp + "_exp" + std::to_string(actualExposure) +
                "_gain" + std::to_string(actualGain) + "_offset" + std::to_string(actualOffset) +
                "_bin" + std::to_string(bin) + "_hwbin" + std::to_string(hardwareBin) +
                "_monobin" + std::to_string(monoBin) +
                "_seq" + std::to_string(sequence);
            const auto rawPath = options.outputDirectory / (stem + ".raw16");
            const auto metadataPath = options.outputDirectory / (stem + ".json");

            long temperature = 0;
            ASI_BOOL automatic = ASI_FALSE;
            const auto temperatureAvailable =
                ASIGetControlValue(info.CameraID, ASI_TEMPERATURE, &temperature, &automatic) == ASI_SUCCESS;
            if (!temperatureAvailable)
            {
                temperature = 0;
            }
            const auto statistics = CalculateStatistics(pixels, width);
            const auto exposureElapsedMilliseconds = std::chrono::duration<double, std::milli>(
                exposureCompleted - exposureStarted).count();
            const auto downloadElapsedMilliseconds = std::chrono::duration<double, std::milli>(
                downloadCompleted - exposureCompleted).count();
            auto temporaryRawPath = rawPath;
            temporaryRawPath += ".partial";
            std::ofstream raw(temporaryRawPath, std::ios::binary | std::ios::trunc);
            if (!raw)
            {
                throw std::runtime_error("Unable to create raw file " + temporaryRawPath.string());
            }
            try
            {
                raw.write(reinterpret_cast<const char*>(pixels.data()), static_cast<std::streamsize>(pixels.size()));
                PublishFile(raw, temporaryRawPath, rawPath);
            }
            catch (...)
            {
                raw.close();
                std::error_code ignored;
                std::filesystem::remove(temporaryRawPath, ignored);
                throw;
            }
            try
            {
                WriteMetadata(
                    metadataPath, info, serial, options, width, height, bin,
                    actualExposure, actualGain, actualOffset, temperature, temperatureAvailable,
                    bandwidth, highSpeedMode, monoBin, hardwareBin, flip,
                    startedUtc, completedUtc, exposureElapsedMilliseconds, downloadElapsedMilliseconds,
                    profilePath.filename().string(), statistics, sequence);
            }
            catch (...)
            {
                std::error_code ignored;
                std::filesystem::remove(rawPath, ignored);
                throw;
            }
            std::cout << rawPath << " mean=" << statistics.mean << " p50=" << statistics.p50
                      << " min=" << statistics.minimum << " max=" << statistics.maximum << '\n';
        }
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "asi-capture: " << exception.what() << '\n';
        return 1;
    }
}
