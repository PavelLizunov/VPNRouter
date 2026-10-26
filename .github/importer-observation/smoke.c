/* Disposable-CI transparency control; not historical film input or a benchmark. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <x264.h>

static int write_nals(FILE *file, x264_nal_t *nals, int count)
{
    for (int i = 0; i < count; ++i)
        if (fwrite(nals[i].p_payload, 1, (size_t)nals[i].i_payload, file)
            != (size_t)nals[i].i_payload)
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
    x264_param_t p;
    if (x264_param_default_preset(&p, "fast", "animation") < 0)
        return 4;
    p.i_width = 854;
    p.i_height = 480;
    p.i_csp = X264_CSP_I420;
    p.i_bitdepth = 8;
    p.i_threads = 1;
    p.i_fps_num = 30;
    p.i_fps_den = 1;
    p.i_timebase_num = 1;
    p.i_timebase_den = 30;
    p.i_keyint_max = 24;
    p.rc.f_rf_constant = 18;
    p.vui.i_colorprim = 1;
    p.vui.i_transfer = 1;
    p.vui.i_colmatrix = 1;
    p.i_log_level = X264_LOG_NONE;
    p.b_repeat_headers = 0;
    x264_t *encoder = x264_encoder_open(&p);
    x264_picture_t input, output;
    if (!encoder || x264_picture_alloc(&input, X264_CSP_I420, 854, 480) < 0)
        return 5;
    x264_nal_t *nals;
    int count;
    if (x264_encoder_headers(encoder, &nals, &count) < 0 || write_nals(file, nals, count))
        return 6;
    for (int index = 0; index < 24; ++index) {
        for (int plane = 0; plane < 3; ++plane)
            for (int row = 0; row < (plane ? 240 : 480); ++row)
                for (int col = 0; col < (plane ? 427 : 854); ++col)
                    input.img.plane[plane][row * input.img.i_stride[plane] + col] =
                        (uint8_t)(plane ? 96 + (row + col + index + plane) % 64
                                       : 24 + (row * 3 + col + index * 7) % 200);
        input.i_pts = 768 + index;
        if (x264_encoder_encode(encoder, &nals, &count, &input, &output) < 0
            || write_nals(file, nals, count))
            return 7;
    }
    while (x264_encoder_delayed_frames(encoder))
        if (x264_encoder_encode(encoder, &nals, &count, NULL, &output) < 0
            || write_nals(file, nals, count))
            return 8;
    x264_picture_clean(&input);
    x264_encoder_close(encoder);
    return fclose(file) != 0;
}
