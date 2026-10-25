/* CI-only observation at the real x264 boundary; never changes encoder inputs. */
#define _GNU_SOURCE
/* Stack-only SHA256 avoids per-frame provider/EVP allocations in this observer. */
#define OPENSSL_API_COMPAT 0x10100000L
#include <dlfcn.h>
#include <fcntl.h>
#include <inttypes.h>
#include <limits.h>
#include <openssl/sha.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <x264.h>

#define STRING_VALUE_(x) #x
#define STRING_VALUE(x) STRING_VALUE_(x)
#define MAX_ENCODERS 64
#define MAX_NALS 4096

static x264_t *(*real_open)(x264_param_t *);
static int (*real_encode)(x264_t *, x264_nal_t **, int *, x264_picture_t *, x264_picture_t *);
static int (*real_headers)(x264_t *, x264_nal_t **, int *);
static void (*real_close)(x264_t *);
static void (*real_parameters)(x264_t *, x264_param_t *);
static pthread_once_t symbols_once = PTHREAD_ONCE_INIT;
static pthread_mutex_t trace_mutex = PTHREAD_MUTEX_INITIALIZER;
static FILE *trace;

/* Fixed storage; IDs name open lifetimes, not reusable native pointer addresses. */
static struct encoder_trace {
    x264_t *handle;
    unsigned id, inputs, outputs, calls, flushes, headers;
} encoders[MAX_ENCODERS];
static unsigned opened;

static void fail(const char *reason)
{
    fprintf(stderr, "x264 input probe: %s\n", reason);
    _exit(125);
}

static void load_symbols(void)
{
    real_open = (x264_t *(*)(x264_param_t *))dlsym(RTLD_NEXT, "x264_encoder_open_" STRING_VALUE(X264_BUILD));
    real_encode = (int (*)(x264_t *, x264_nal_t **, int *, x264_picture_t *, x264_picture_t *))dlsym(RTLD_NEXT, "x264_encoder_encode");
    real_headers = (int (*)(x264_t *, x264_nal_t **, int *))dlsym(RTLD_NEXT, "x264_encoder_headers");
    real_close = (void (*)(x264_t *))dlsym(RTLD_NEXT, "x264_encoder_close");
    real_parameters = (void (*)(x264_t *, x264_param_t *))dlsym(RTLD_NEXT, "x264_encoder_parameters");
    if (!real_open || !real_encode || !real_headers || !real_close || !real_parameters)
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

static void digest_hex(const unsigned char digest[SHA256_DIGEST_LENGTH], char hex[65])
{
    for (unsigned int i = 0; i < SHA256_DIGEST_LENGTH; ++i)
        snprintf(hex + i * 2, 3, "%02x", digest[i]);
}

static void plane_hash(const uint8_t *data, int stride, int width, int height, char hex[65])
{
    unsigned char digest[SHA256_DIGEST_LENGTH];
    SHA256_CTX ctx;
    if (!data || stride < width || width <= 0 || height <= 0)
        fail("invalid visible plane");
    if (SHA256_Init(&ctx) != 1)
        fail("cannot initialize SHA256");
    for (int row = 0; row < height; ++row)
        if (SHA256_Update(&ctx, data + (size_t)row * (size_t)stride, (size_t)width) != 1)
            fail("cannot hash plane");
    if (SHA256_Final(digest, &ctx) != 1)
        fail("cannot finish SHA256");
    digest_hex(digest, hex);
}

/* Read returned payloads before any subsequent native call can invalidate them. */
static void nal_hash(x264_nal_t *nals, int count, int result, char hex[65])
{
    unsigned char digest[SHA256_DIGEST_LENGTH];
    SHA256_CTX ctx;
    size_t bytes = 0;
    if (result < 0 || count < 0 || count > MAX_NALS || (count && !nals))
        fail("invalid native NAL result");
    if (SHA256_Init(&ctx) != 1)
        fail("cannot initialize NAL SHA256");
    for (int i = 0; i < count; ++i) {
        if (nals[i].i_payload <= 0 || !nals[i].p_payload)
            fail("invalid returned NAL payload");
        bytes += (size_t)nals[i].i_payload;
        if (bytes > INT_MAX || SHA256_Update(&ctx, nals[i].p_payload, (size_t)nals[i].i_payload) != 1)
            fail("cannot hash returned NAL payload");
    }
    if (bytes != (size_t)result || SHA256_Final(digest, &ctx) != 1)
        fail("returned NAL byte count mismatch");
    digest_hex(digest, hex);
}

/* Called under trace_mutex. Closed slots are never reused. */
static struct encoder_trace *lookup(x264_t *encoder)
{
    for (unsigned i = 0; i < opened; ++i)
        if (encoders[i].handle == encoder)
            return &encoders[i];
    fail("call outside an observed open lifetime");
    return NULL;
}

static void nal_sizes(FILE *file, x264_nal_t *nals, int count)
{
    for (int i = 0; i < count; ++i)
        fprintf(file, "\t%d", nals[i].i_payload);
    fputc('\n', file);
    flush_trace(file);
}

x264_t *x264_encoder_open(x264_param_t *parameters)
{
    pthread_once(&symbols_once, load_symbols);
    x264_t *encoder = real_open(parameters);
    if (encoder && getenv("FFRAMES_INPUT_TRACE")) {
        x264_param_t actual;
        real_parameters(encoder, &actual);
        pthread_mutex_lock(&trace_mutex);
        if (opened == MAX_ENCODERS)
            fail("too many encoder lifetimes");
        struct encoder_trace *item = &encoders[opened++];
        item->handle = encoder;
        item->id = opened;
        FILE *file = trace_file();
        fprintf(file, "open\t%u\t%d\t%d\t%d\t%d\t%d\t%d\t%d\n", item->id,
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
    int observing = getenv("FFRAMES_INPUT_TRACE") != NULL;
    if (input && observing) {
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
        struct encoder_trace *item = lookup(encoder);
        FILE *file = trace_file();
        fprintf(file, "frame\t%u\t%" PRId64 "\t%s\t%s\t%s\n", item->id,
                input->i_pts, hashes[0], hashes[1], hashes[2]);
        ++item->inputs;
        flush_trace(file);
        pthread_mutex_unlock(&trace_mutex);
    }
    int result = real_encode(encoder, nals, count, input, output);
    if (observing) {
        char hash[65];
        if (result < 0)
            fail("native encode failed");
        if (!nals || !count)
            fail("native NAL result pointers missing");
        x264_nal_t *payloads = *count ? *nals : NULL;
        nal_hash(payloads, *count, result, hash);
        if (*count && !output)
            fail("output picture missing");
        pthread_mutex_lock(&trace_mutex);
        struct encoder_trace *item = lookup(encoder);
        FILE *file = trace_file();
        fprintf(file, "output\t%u\t%u\t%d\t%d\t%d\t", item->id, item->calls++,
                input == NULL, result, *count);
        if (*count) {
            fprintf(file, "%" PRId64, output->i_pts);
            ++item->outputs;
        } else {
            fputc('-', file);
        }
        fprintf(file, "\t%s", hash);
        item->flushes += input == NULL;
        nal_sizes(file, payloads, *count);
        pthread_mutex_unlock(&trace_mutex);
    }
    return result;
}

int x264_encoder_headers(x264_t *encoder, x264_nal_t **nals, int *count)
{
    pthread_once(&symbols_once, load_symbols);
    int result = real_headers(encoder, nals, count);
    if (getenv("FFRAMES_INPUT_TRACE")) {
        char hash[65];
        if (result < 0)
            fail("native headers failed");
        if (!nals || !count)
            fail("native header result pointers missing");
        x264_nal_t *payloads = *count ? *nals : NULL;
        nal_hash(payloads, *count, result, hash);
        pthread_mutex_lock(&trace_mutex);
        struct encoder_trace *item = lookup(encoder);
        FILE *file = trace_file();
        fprintf(file, "headers\t%u\t%d\t%d\t%s", item->id, result, *count, hash);
        ++item->headers;
        nal_sizes(file, payloads, *count);
        pthread_mutex_unlock(&trace_mutex);
    }
    return result;
}

void x264_encoder_close(x264_t *encoder)
{
    pthread_once(&symbols_once, load_symbols);
    struct encoder_trace *item = NULL;
    if (getenv("FFRAMES_INPUT_TRACE")) {
        pthread_mutex_lock(&trace_mutex);
        item = lookup(encoder);
        item->handle = NULL;
        pthread_mutex_unlock(&trace_mutex);
    }
    real_close(encoder);
    if (item) {
        pthread_mutex_lock(&trace_mutex);
        FILE *file = trace_file();
        fprintf(file, "close\t%u\t%u\t%u\t%u\t%u\t%u\n", item->id, item->inputs,
                item->outputs, item->calls, item->flushes, item->headers);
        flush_trace(file);
        pthread_mutex_unlock(&trace_mutex);
    }
}
