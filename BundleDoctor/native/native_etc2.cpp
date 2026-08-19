#include <cstdint>
#include <cstdlib>
#include <vector>
#include <cmath>
#include <Etc.h>

extern "C" {

// alpha = false -> ETC2 RGB8 (Unity TextureFormat 45, 4 bpp)
// alpha = true  -> ETC2 RGBA8 (Unity TextureFormat 47, 8 bpp)
int etc2_encode_rgba8(
    const std::uint8_t* rgba,
    std::uint32_t width,
    std::uint32_t height,
    bool alpha,
    float effort,
    std::uint32_t jobs,
    std::uint8_t** out_data,
    std::uint32_t* out_size)
{
    if (!rgba || !out_data || !out_size || width == 0 || height == 0)
        return 1;

    try {
        const std::size_t pixelCount = static_cast<std::size_t>(width) * height;
        std::vector<Etc::ColorFloatRGBA> pixels(pixelCount);

        for (std::size_t i = 0; i < pixelCount; ++i) {
            const std::uint8_t* p = rgba + i * 4;
            pixels[i] = Etc::ColorFloatRGBA::ConvertFromRGBA8(p[0], p[1], p[2], p[3]);
        }

        const Etc::Image::Format format = alpha
            ? Etc::Image::Format::RGBA8
            : Etc::Image::Format::RGB8;

        // 0 is the fast/normal default recommended by Etc2Comp/Godot. The public
        // encoder accepts an effort in [0,100]. Jobs are capped at the requested
        // number by the library itself.
        constexpr Etc::ErrorMetric metric = Etc::ErrorMetric::RGBX;
        float* source = reinterpret_cast<float*>(pixels.data());

        unsigned char* encoded = nullptr;
        unsigned int encodedLength = 0;
        unsigned int extendedWidth = 0;
        unsigned int extendedHeight = 0;
        int encodingTimeMs = 0;

        auto status = Etc::Encode(
            source,
            width,
            height,
            format,
            metric,
            effort,
            jobs,
            jobs,
            &encoded,
            &encodedLength,
            &extendedWidth,
            &extendedHeight,
            &encodingTimeMs);

        (void)status;
        if (!encoded || encodedLength == 0) {
            delete[] encoded;
            return 2;
        }

        *out_data = encoded;
        *out_size = encodedLength;
        return 0;
    }
    catch (...) {
        *out_data = nullptr;
        *out_size = 0;
        return 3;
    }
}

void etc2_free(std::uint8_t* buffer)
{
    delete[] buffer;
}

}
