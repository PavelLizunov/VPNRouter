/* CI-only r3108 importer hooks. No input writes or optional-pointee reads.
 * Included after common/common.h; patcher binds encoder/frame sources by SHA256.
 * The only reads are visible input and the allocated post-mod16 coded rectangle.
 */
#ifndef FFRAMES_IMPORTER_OBSERVER_H
#define FFRAMES_IMPORTER_OBSERVER_H
#define OPENSSL_API_COMPAT 0x10100000L
#include <openssl/sha.h>
#include <fcntl.h>
#include <inttypes.h>
#include <unistd.h>

#if BIT_DEPTH == 8
static FILE *fframes_import_file;
static x264_t *fframes_import_encoder;
static unsigned fframes_import_count;
static int fframes_import_pending;

static void fframes_import_fail(const char *reason)
{
    fprintf(stderr, "bounded importer observer: %s\n", reason);
    _exit(125);
}

static void fframes_import_hash(const uint8_t *data, int stride, int width,
                               int height, char hex[65])
{
    SHA256_CTX ctx;
    unsigned char hash[SHA256_DIGEST_LENGTH];
    if (!data || stride < width || width <= 0 || height <= 0
        || SHA256_Init(&ctx) != 1)
        fframes_import_fail("invalid source-bounded rectangle");
    for (int row = 0; row < height; ++row)
        if (SHA256_Update(&ctx, data + (size_t)row * (size_t)stride, (size_t)width) != 1)
            fframes_import_fail("rectangle hash failed");
    if (SHA256_Final(hash, &ctx) != 1)
        fframes_import_fail("rectangle hash finalization failed");
    for (unsigned i = 0; i < SHA256_DIGEST_LENGTH; ++i)
        snprintf(hex + 2 * i, 3, "%02x", hash[i]);
}

static void fframes_import_flush(void)
{
    if (ferror(fframes_import_file) || fflush(fframes_import_file) != 0)
        fframes_import_fail("cannot persist importer evidence");
}

static void fframes_import_entry(x264_t *h, x264_picture_t *input)
{
    const char *path = getenv("FFRAMES_IMPORTER_TRACE");
    if (!path)
        return;
    if (h->param.i_width != 854 || h->param.i_height != 480
        || h->param.i_csp != X264_CSP_I420 || h->param.i_threads != 1
        || h->param.b_interlaced || h->param.b_sliced_threads
        || h->mb.i_mb_width != 54 || h->mb.i_mb_height != 30
        || input->img.i_csp != X264_CSP_I420 || input->img.i_plane != 3
        || input->i_pts != (int64_t)(768 + fframes_import_count)
        || fframes_import_count >= 24 || fframes_import_pending)
        fframes_import_fail("unexpected importer lifetime/settings/PTS");
    if (!fframes_import_file) {
        int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0600);
        if (fd < 0 || !(fframes_import_file = fdopen(fd, "w")))
            fframes_import_fail("cannot create exclusive importer trace");
        fframes_import_encoder = h;
        fprintf(fframes_import_file,
                "identity\t1\t854\t480\t864\t480\t%u\t8\t1\n", h->param.cpu);
    }
    if (fframes_import_encoder != h)
        fframes_import_fail("more than one encoder lifetime");
    char hashes[3][65];
    for (int p = 0; p < 3; ++p)
        fframes_import_hash(input->img.plane[p], input->img.i_stride[p],
                            p ? 427 : 854, p ? 240 : 480, hashes[p]);
    fprintf(fframes_import_file, "entry\t1\t%u\t%" PRId64
            "\t%d\t%d\t%d\t%s\t%s\t%s\n", fframes_import_count, input->i_pts,
            input->img.i_stride[0], input->img.i_stride[1], input->img.i_stride[2],
            hashes[0], hashes[1], hashes[2]);
    fframes_import_pending = 1;
    fframes_import_flush();
}

static void fframes_import_coded(x264_t *h, x264_frame_t *frame)
{
    if (!getenv("FFRAMES_IMPORTER_TRACE"))
        return;
    /* frame_new(): NV12 i_width[1] counts chroma pairs, stride counts bytes.
     * Each row here is the coded rectangle, never backing stride or outer bands.
     */
    if (!fframes_import_pending || h != fframes_import_encoder
        || frame->i_pts != (int64_t)(768 + fframes_import_count)
        || frame->i_csp != X264_CSP_NV12 || frame->i_plane != 2
        || frame->b_fdec || frame->i_width[0] != 864 || frame->i_lines[0] != 480
        || frame->i_width[1] != 432 || frame->i_lines[1] != 240
        || frame->i_stride[0] < 864 || frame->i_stride[1] < 864)
        fframes_import_fail("unexpected allocated coded-plane geometry");
    char hashes[2][65];
    fframes_import_hash(frame->plane[0], frame->i_stride[0], 864, 480, hashes[0]);
    fframes_import_hash(frame->plane[1], frame->i_stride[1], 864, 240, hashes[1]);
    fprintf(fframes_import_file, "coded\t1\t%u\t%" PRId64
            "\t%d\t%d\t%s\t%s\n", fframes_import_count, frame->i_pts,
            frame->i_stride[0], frame->i_stride[1], hashes[0], hashes[1]);
    fframes_import_pending = 0;
    ++fframes_import_count;
    if (fframes_import_count == 24)
        fprintf(fframes_import_file, "complete\t1\t24\n");
    fframes_import_flush();
}
#else
static void fframes_import_entry(x264_t *h, x264_picture_t *input)
{
    (void)h;
    (void)input;
    if (getenv("FFRAMES_IMPORTER_TRACE"))
        _exit(125);
}
static void fframes_import_coded(x264_t *h, x264_frame_t *frame)
{
    (void)h;
    (void)frame;
    if (getenv("FFRAMES_IMPORTER_TRACE"))
        _exit(125);
}
#endif
#endif
