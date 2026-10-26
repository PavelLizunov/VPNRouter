/* Disposable-CI smoke fixture for probe transparency, not a benchmark. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <x264.h>

static int write_nals(FILE *file, x264_nal_t *nals, int count)
{
    for (int index = 0; index < count; ++index)
        if (fwrite(nals[index].p_payload, 1, (size_t)nals[index].i_payload, file)
            != (size_t)nals[index].i_payload)
            return 1;
    return 0;
}

int main(int argc, char **argv)
{
    if (argc != 2)
        return 2;
    FILE *file = fopen(argv[1], "wbx");
    if (!file)
        return 3;
    x264_param_t parameters;
    if (x264_param_default_preset(&parameters, "fast", "animation") < 0)
        return 4;
    parameters.i_width = 854;
    parameters.i_height = 480;
    parameters.i_csp = X264_CSP_I420;
    parameters.i_threads = 1;
    parameters.i_fps_num = 30;
    parameters.i_fps_den = 1;
    parameters.i_log_level = X264_LOG_NONE;
    parameters.b_repeat_headers = 0;
    x264_t *encoder = x264_encoder_open(&parameters);
    x264_picture_t input, output;
    if (!encoder || x264_picture_alloc(&input, X264_CSP_I420, 854, 480) < 0)
        return 5;
    x264_nal_t *headers;
    int header_count;
    if (x264_encoder_headers(encoder, &headers, &header_count) < 0
        || write_nals(file, headers, header_count))
        return 8;
    for (int index = 0; index < 3; ++index) {
        for (int plane = 0; plane < 3; ++plane)
            for (int row = 0; row < (plane ? 240 : 480); ++row)
                memset(input.img.plane[plane] + row * input.img.i_stride[plane],
                       plane ? 128 : 32 + index * 20, (size_t)(plane ? 427 : 854));
        input.i_pts = index;
        x264_nal_t *nals;
        int count;
        if (x264_encoder_encode(encoder, &nals, &count, &input, &output) < 0
            || write_nals(file, nals, count))
            return 6;
    }
    while (x264_encoder_delayed_frames(encoder)) {
        x264_nal_t *nals;
        int count;
        if (x264_encoder_encode(encoder, &nals, &count, NULL, &output) < 0
            || write_nals(file, nals, count))
            return 7;
    }
    x264_picture_clean(&input);
    x264_encoder_close(encoder);
    return fclose(file) != 0;
}
