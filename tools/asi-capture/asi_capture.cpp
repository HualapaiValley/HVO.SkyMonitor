#include "ASICamera2.h"

#include <algorithm>
#include <chrono>
#include <climits>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
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
            options.count = static_cast<int>(ParseLong(argv[++index], "count"));
        }
        else if (argument == "--camera-index" && index + 1 < argc)
        {
            options.cameraIndex = static_cast<int>(ParseLong(argv[++index], "camera index"));
        }
        else
        {
            throw std::invalid_argument(
                "Usage: asi-capture [--output-dir PATH] [--exposure-us N] [--gain N] "
                "[--offset N] [--count N] [--camera-index N]");
        }
    }

    if (options.exposureMicroseconds <= 0 || options.count <= 0 || options.count > 100 || options.cameraIndex < 0)
    {
        throw std::invalid_argument("Exposure, count, or camera index is outside the supported range.");
    }
    return options;
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
};

Statistics CalculateStatistics(const std::vector<unsigned char>& bytes, int bitDepth)
{
    const auto sampleCount = bytes.size() / 2;
    std::vector<std::uint64_t> histogram(1U << 16U);
    std::uint64_t total = 0;
    std::uint16_t minimum = UINT16_MAX;
    std::uint16_t maximum = 0;
    for (std::size_t index = 0; index < sampleCount; index++)
    {
        const auto sample = static_cast<std::uint16_t>(
            bytes[index * 2] | static_cast<std::uint16_t>(bytes[index * 2 + 1]) << 8U);
        histogram[sample]++;
        total += sample;
        minimum = std::min(minimum, sample);
        maximum = std::max(maximum, sample);
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

    (void)bitDepth;
    const auto nearContainerMaximumCount =
        histogram[UINT16_MAX] + histogram[UINT16_MAX - 1] +
        histogram[UINT16_MAX - 2] + histogram[UINT16_MAX - 3];
    return Statistics{
        minimum,
        maximum,
        static_cast<double>(total) / static_cast<double>(sampleCount),
        percentile(0.01),
        percentile(0.50),
        percentile(0.99),
        histogram[0],
        nearContainerMaximumCount};
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
    long bandwidth,
    long highSpeedMode,
    const std::string& startedUtc,
    const std::string& completedUtc,
    const Statistics& statistics,
    int sequence)
{
    std::ofstream output(path);
    if (!output)
    {
        throw std::runtime_error("Unable to create metadata file " + path.string());
    }
    output << std::setprecision(17)
           << "{\n"
           << "  \"cameraModel\": \"" << JsonEscape(info.Name) << "\",\n"
           << "  \"cameraSerial\": \"" << serial << "\",\n"
           << "  \"sdkVersion\": \"" << JsonEscape(ASIGetSDKVersion()) << "\",\n"
           << "  \"pixelFormat\": \"RAW16\",\n"
           << "  \"width\": " << width << ",\n"
           << "  \"height\": " << height << ",\n"
           << "  \"bin\": " << bin << ",\n"
           << "  \"bitDepth\": " << info.BitDepth << ",\n"
           << "  \"pixelSizeMicrons\": " << info.PixelSize << ",\n"
           << "  \"electronsPerAduReported\": " << info.ElecPerADU << ",\n"
           << "  \"isColorCamera\": " << (info.IsColorCam ? "true" : "false") << ",\n"
           << "  \"bayerPattern\": " << static_cast<int>(info.BayerPattern) << ",\n"
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
           << "  \"sensorTemperatureC\": " << temperature / 10.0 << ",\n"
           << "  \"startedUtc\": \"" << startedUtc << "\",\n"
           << "  \"completedUtc\": \"" << completedUtc << "\",\n"
           << "  \"sequence\": " << sequence << ",\n"
           << "  \"statistics\": {\n"
           << "    \"minimumAdu\": " << statistics.minimum << ",\n"
           << "    \"maximumAdu\": " << statistics.maximum << ",\n"
           << "    \"meanAdu\": " << statistics.mean << ",\n"
           << "    \"p01Adu\": " << statistics.p01 << ",\n"
           << "    \"p50Adu\": " << statistics.p50 << ",\n"
           << "    \"p99Adu\": " << statistics.p99 << ",\n"
           << "    \"zeroCount\": " << statistics.zeroCount << ",\n"
           << "    \"nearContainerMaximumCount\": " << statistics.nearContainerMaximumCount << "\n"
           << "  }\n"
           << "}\n";
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
        if (!SupportsRaw16(info))
        {
            throw std::runtime_error(std::string(info.Name) + " does not advertise RAW16 output.");
        }

        Check(ASIOpenCamera(info.CameraID), "ASIOpenCamera");
        CameraCloser closer{info.CameraID};
        Check(ASIInitCamera(info.CameraID), "ASIInitCamera");
        const auto controls = ReadControlCaps(info.CameraID);

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

        Check(ASISetROIFormat(
            info.CameraID,
            static_cast<int>(info.MaxWidth),
            static_cast<int>(info.MaxHeight),
            1,
            ASI_IMG_RAW16), "ASISetROIFormat");
        Check(ASISetStartPos(info.CameraID, 0, 0), "ASISetStartPos");

        int width = 0;
        int height = 0;
        int bin = 0;
        ASI_IMG_TYPE imageType = ASI_IMG_END;
        Check(ASIGetROIFormat(info.CameraID, &width, &height, &bin, &imageType), "ASIGetROIFormat");
        if (imageType != ASI_IMG_RAW16)
        {
            throw std::runtime_error("Camera did not retain RAW16 mode.");
        }

        const auto byteCount = static_cast<std::size_t>(width) * static_cast<std::size_t>(height) * 2U;
        if (byteCount > static_cast<std::size_t>(LONG_MAX))
        {
            throw std::overflow_error("RAW16 buffer exceeds the SDK buffer-size type.");
        }
        std::vector<unsigned char> pixels(byteCount);
        std::filesystem::create_directories(options.outputDirectory);

        const auto actualExposure = ReadControl(info.CameraID, ASI_EXPOSURE);
        const auto actualGain = ReadControl(info.CameraID, ASI_GAIN);
        const auto actualOffset = ReadControl(info.CameraID, ASI_OFFSET);
        const auto bandwidth = controls.contains(ASI_BANDWIDTHOVERLOAD)
            ? ReadControl(info.CameraID, ASI_BANDWIDTHOVERLOAD) : -1;
        const auto highSpeedMode = controls.contains(ASI_HIGH_SPEED_MODE)
            ? ReadControl(info.CameraID, ASI_HIGH_SPEED_MODE) : -1;
        const auto serial = SerialNumber(info.CameraID);

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
            Check(ASIGetDataAfterExp(info.CameraID, pixels.data(), static_cast<long>(pixels.size())),
                "ASIGetDataAfterExp");
            const auto completedUtc = UtcTimestamp(false);
            const auto fileTimestamp = UtcTimestamp(true);
            const auto stem = fileTimestamp + "_exp" + std::to_string(actualExposure) +
                "_gain" + std::to_string(actualGain) + "_offset" + std::to_string(actualOffset) +
                "_seq" + std::to_string(sequence);
            const auto rawPath = options.outputDirectory / (stem + ".raw16");
            const auto metadataPath = options.outputDirectory / (stem + ".json");

            std::ofstream raw(rawPath, std::ios::binary);
            if (!raw)
            {
                throw std::runtime_error("Unable to create raw file " + rawPath.string());
            }
            raw.write(reinterpret_cast<const char*>(pixels.data()), static_cast<std::streamsize>(pixels.size()));
            raw.close();

            long temperature = 0;
            ASI_BOOL automatic = ASI_FALSE;
            if (ASIGetControlValue(info.CameraID, ASI_TEMPERATURE, &temperature, &automatic) != ASI_SUCCESS)
            {
                temperature = 0;
            }
            const auto statistics = CalculateStatistics(pixels, info.BitDepth);
            WriteMetadata(
                metadataPath, info, serial, options, width, height, bin,
                actualExposure, actualGain, actualOffset, temperature, bandwidth, highSpeedMode,
                startedUtc, completedUtc, statistics, sequence);
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
