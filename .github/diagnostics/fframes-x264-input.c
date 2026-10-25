/* CI-only observation at the real x264 boundary; never changes encoder inputs. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <inttypes.h>
#include <openssl/evp.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <x264.h>

#define STRING_VALUE_(x) #x
#define STRING_VALUE(x) STRING_VALUE_(x)

static x264_t *(*real_open)(x264_param_t *);
static int (*real_encode)(x264_t *, x264_nal_t **, int *, x264_picture_t *, x264_picture_t *);
static void (*real_parameters)(x264_t *, x264_param_t *);
static pthread_once_t symbols_once = PTHREAD_ONCE_INIT;
static pthread_mutex_t trace_mutex = PTHREAD_MUTEX_INITIALIZER;
static FILE *trace;

static void fail(const char *reason)
{
    fprintf(stderr, "x264 input probe: %s\n", reason);
    _exit(125);
}

static void load_symbols(void)
{
    real_open = (x264_t *(*)(x264_param_t *))dlsym(RTLD_NEXT, "x264_encoder_open_" STRING_VALUE(X264_BUILD));
    real_encode = (int (*)(x264_t *, x264_nal_t **, int *, x264_picture_t *, x264_picture_t *))dlsym(RTLD_NEXT, "x264_encoder_encode");
    real_parameters = (void (*)(x264_t *, x264_param_t *))dlsym(RTLD_NEXT, "x264_encoder_parameters");
    if (!real_open || !real_encode || !real_parameters)
        fail("real x264 symbols unavailable");
}

/* Called under trace_mutex. One exclusively created trace per process. */
static FILE *trace_file(void)
{
    const char *directory = getenv("FFRAMES_INPUT_TRACE");
    if (!directory)
        return NULL;
    if (!trace) {
        const char *style = getenv("FILM_STYLE");
        char path[4096];
        if (!style || !*style)
            fail("FILM_STYLE is required");
        for (const char *p = style; *p; ++p)
            if ((*p < 'a' || *p > 'z') && *p != '-')
                fail("unsafe style label");
        int length = snprintf(path, sizeof(path), "%s/%s-%ld.tsv", directory, style, (long)getpid());
        if (length < 0 || (size_t)length >= sizeof(path))
            fail("trace path too long");
        int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0600);
        if (fd < 0 || !(trace = fdopen(fd, "w")))
            fail("cannot create exclusive trace");
    }
    return trace;
}

static void flush_trace(FILE *file)
{
    if (ferror(file) || fflush(file) != 0)
        fail("cannot persist trace");
}

static void plane_hash(const uint8_t *data, int stride, int width, int height, char hex[65])
{
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int size = 0;
    if (!data || stride < width || width <= 0 || height <= 0)
        fail("invalid visible plane");
    EVP_MD_CTX *ctx = EVP_MD_CTX_new();
    if (!ctx || EVP_DigestInit_ex(ctx, EVP_sha256(), NULL) != 1)
        fail("cannot initialize SHA256");
    for (int row = 0; row < height; ++row)
        if (EVP_DigestUpdate(ctx, data + (size_t)row * (size_t)stride, (size_t)width) != 1)
            fail("cannot hash plane");
    if (EVP_DigestFinal_ex(ctx, digest, &size) != 1 || size != 32)
        fail("cannot finish SHA256");
    EVP_MD_CTX_free(ctx);
    for (unsigned int i = 0; i < size; ++i)
        snprintf(hex + i * 2, 3, "%02x", digest[i]);
}

x264_t *x264_encoder_open(x264_param_t *parameters)
{
    pthread_once(&symbols_once, load_symbols);
    x264_t *encoder = real_open(parameters);
    if (encoder && getenv("FFRAMES_INPUT_TRACE")) {
        x264_param_t actual;
        real_parameters(encoder, &actual);
        pthread_mutex_lock(&trace_mutex);
        FILE *file = trace_file();
        fprintf(file, "open\t%p\t%d\t%d\t%d\t%d\t%d\t%d\t%d\n", (void *)encoder,
                actual.i_width, actual.i_height, actual.i_csp, actual.i_threads,
                actual.i_lookahead_threads, actual.i_sync_lookahead, actual.b_deterministic);
        flush_trace(file);
        pthread_mutex_unlock(&trace_mutex);
    }
    return encoder;
}

int x264_encoder_encode(x264_t *encoder, x264_nal_t **nals, int *count,
                        x264_picture_t *input, x264_picture_t *output)
{
    pthread_once(&symbols_once, load_symbols);
    if (input && getenv("FFRAMES_INPUT_TRACE")) {
        x264_param_t actual;
        char hashes[3][65];
        real_parameters(encoder, &actual);
        if (input->img.i_csp != X264_CSP_I420 || input->img.i_plane != 3)
            fail("only exact 8-bit planar I420 is supported");
        for (int plane = 0; plane < 3; ++plane)
            plane_hash(input->img.plane[plane], input->img.i_stride[plane],
                       plane ? (actual.i_width + 1) / 2 : actual.i_width,
                       plane ? (actual.i_height + 1) / 2 : actual.i_height, hashes[plane]);
        pthread_mutex_lock(&trace_mutex);
        FILE *file = trace_file();
        fprintf(file, "frame\t%p\t%" PRId64 "\t%s\t%s\t%s\n", (void *)encoder,
                input->i_pts, hashes[0], hashes[1], hashes[2]);
        flush_trace(file);
        pthread_mutex_unlock(&trace_mutex);
    }
    return real_encode(encoder, nals, count, input, output);
}
