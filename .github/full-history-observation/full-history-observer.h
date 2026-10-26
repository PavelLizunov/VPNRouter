/* Disposable, orig-r3108-only hooks. No optional-pointee reads or float evaluation.
 * Included once per TU, after common.h / the private ratecontrol definition.
 * Source contract and anchors: instrument.py. Build/link/runtime proof belongs to CI.
 */
#if defined(FFH_ENCODER_PART) == defined(FFH_RATECONTROL_PART)
#error "Select exactly one full-history implementation part"
#endif
#define OPENSSL_API_COMPAT 0x10100000L
#include <limits.h>
#include <float.h>
#include <fcntl.h>
#include <inttypes.h>
#include <sys/stat.h>
#include <unistd.h>
#if CHAR_BIT != 8 || FLT_RADIX != 2 || FLT_MANT_DIG != 24 || FLT_MAX_EXP != 128 || FLT_MIN_EXP != -125
#error "Full-history observer requires octets and IEEE binary32"
#endif
typedef char ffh_float_size[(sizeof(float) == 4) ? 1 : -1];
typedef char ffh_uint16_size[(sizeof(uint16_t) == 2) ? 1 : -1];
typedef char ffh_uint32_size[(sizeof(uint32_t) == 4) ? 1 : -1];

#if BIT_DEPTH == 8
extern int ffh8_enabled __attribute__((visibility("hidden")));
void ffh8_qpm_bytes(x264_t *, const unsigned char [4], int)
    __attribute__((visibility("hidden")));
#if defined(FFH_RATECONTROL_PART)
typedef char ffh_qpm_type[__builtin_types_compatible_p(__typeof__(((x264_ratecontrol_t *)0)->qpm), float) ? 1 : -1];
static void ffh_qpm(x264_t *h)
{
    if (!ffh8_enabled)
        return;
    unsigned char raw[4];
    memcpy(raw, &h->rc->qpm, sizeof raw);
    ffh8_qpm_bytes(h, raw, h->rc->b_vbv);
}
#else
#include <openssl/sha.h>
typedef char ffh_offset_type[__builtin_types_compatible_p(__typeof__(((x264_frame_t *)0)->f_qp_offset), float *) ? 1 : -1];
typedef char ffh_offset_aq_type[__builtin_types_compatible_p(__typeof__(((x264_frame_t *)0)->f_qp_offset_aq), float *) ? 1 : -1];
typedef char ffh_inv_type[__builtin_types_compatible_p(__typeof__(((x264_frame_t *)0)->i_inv_qscale_factor), uint16_t *) ? 1 : -1];
int ffh8_enabled __attribute__((visibility("hidden")));
static FILE *ffh_log, *ffh_pixels, *ffh_state;
static x264_t *ffh_active;
static unsigned ffh_expected, ffh_id, ffh_closes, ffh_next, ffh_pending;
static unsigned ffh_seen[5]; /* entry,coded,post_aq,qpm,pre_slice; independent sets */
static int ffh_qpm_pending = -1, ffh_is_closing;
static uint64_t ffh_pixel_offset, ffh_state_offset;
#define FFH_ALL ((1u << 24) - 1)

static void ffh_fail(const char *reason)
{
    /* Preserve whatever each stream could persist, even on a partial stage. */
    if (ffh_pixels) fflush(ffh_pixels);
    if (ffh_state) fflush(ffh_state);
    if (ffh_log) { fprintf(ffh_log, "failure\t%s\n", reason); fflush(ffh_log); }
    fprintf(stderr, "full-history observer: %s\n", reason);
    _exit(125);
}
static void ffh_flush(void)
{
    if (fflush(ffh_pixels) || ferror(ffh_pixels) || fflush(ffh_state)
        || ferror(ffh_state) || fflush(ffh_log) || ferror(ffh_log))
        ffh_fail("cannot persist full-history evidence");
}
static FILE *ffh_create(int dir, const char *name)
{
    int fd = openat(dir, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
    FILE *out;
    if (fd < 0 || !(out = fdopen(fd, "wb")))
        ffh_fail("cannot create exclusive internal evidence file");
    return out;
}
static uint32_t ffh_bits(const void *p)
{
    uint32_t bits;
    memcpy(&bits, p, sizeof bits);
    return bits;
}
static void ffh_gate(x264_t *h)
{
    if (!h || h->param.i_width != 854 || h->param.i_height != 480
        || h->param.i_csp != X264_CSP_I420 || h->param.i_bitdepth != 8
        || h->param.i_threads != 1 || h->i_thread_frames != 1
        || h->param.i_lookahead_threads != 1 || h->param.i_sync_lookahead != 0
        || h->param.b_sliced_threads || h->param.b_interlaced || h->param.b_fake_interlaced
        || h->param.b_pic_struct || h->mb.b_interlaced
        || h->mb.i_mb_width != 54 || h->mb.i_mb_height != 30
        || h->mb.i_mb_stride != 54 || h->mb.i_mb_count != 1620
        || !h->frames.b_have_lowres || h->param.rc.i_aq_mode != 1
        || ffh_bits(&h->param.rc.f_aq_strength) != UINT32_C(0x3f19999a)
        || h->param.rc.b_mb_tree != 1 || h->param.rc.b_stat_read || h->param.rc.b_stat_write
        || h->param.rc.i_vbv_max_bitrate || h->param.rc.i_vbv_buffer_size
        || h->param.rc.i_zones || h->param.rc.zones || h->param.rc.psz_zones
        || h->param.psz_cqm_file || h->param.psz_dump_yuv || h->param.b_opencl
        || h->param.opencl_device_id || h->param.psz_clbin_file
        || h->param.nalu_process)
        ffh_fail("unsupported native geometry/AQ/lowres/thread/VBV/stat/optional configuration");
    /* Default unused rc.psz_stat_in/out presence is deliberately not dereferenced. */
}
static void ffh_check(x264_t *h)
{
    if (!ffh_active || h != ffh_active || ffh_is_closing)
        ffh_fail("hook outside active encoder lifetime");
    ffh_gate(h);
}
static unsigned ffh_local(x264_frame_t *f)
{
    if (!f || f->i_frame < 0 || f->i_frame >= 24
        || f->i_pts != (int64_t)((ffh_id - 1) * 24 + f->i_frame)
        || f->param || f->mb_info || f->mb_info_free || f->extra_sei.num_payloads
        || f->extra_sei.payloads || f->extra_sei.sei_free)
        ffh_fail("unsupported actual consumed frame identity/optional payload");
    return (unsigned)f->i_frame;
}
static void ffh_mark(unsigned stage, unsigned local)
{
    if (local >= 24 || (ffh_seen[stage] & (1u << local)))
        ffh_fail("duplicate or invalid phase identity");
    ffh_seen[stage] |= 1u << local;
}
/* Each descriptor is type,width,height,source_stride,packed_offset,size,SHA256.
 * Raw streams are gapless. Hash/write only packed initialized rows, never slack.
 */
static void ffh_raw(const char *name, const char *type, const void *ptr,
                    int stride, unsigned width, unsigned height, int state)
{
    FILE *out = state ? ffh_state : ffh_pixels;
    uint64_t *offset = state ? &ffh_state_offset : &ffh_pixel_offset;
    unsigned bytes = width * (!strcmp(type, "f32") ? 4 : !strcmp(type, "u16") ? 2 : 1);
    SHA256_CTX ctx;
    unsigned char digest[32];
    char hex[65];
    if (!ptr || stride < (int)bytes || !width || !height || SHA256_Init(&ctx) != 1)
        ffh_fail("invalid initialized raw extent");
    for (unsigned row = 0; row < height; ++row) {
        const unsigned char *p = (const unsigned char *)ptr + (size_t)row * (size_t)stride;
        if (fwrite(p, 1, bytes, out) != bytes || SHA256_Update(&ctx, p, bytes) != 1)
            ffh_fail("raw extent write/hash failed");
    }
    if (SHA256_Final(digest, &ctx) != 1)
        ffh_fail("raw extent hash finalization failed");
    for (unsigned i = 0; i < 32; ++i) snprintf(hex + 2*i, 3, "%02x", digest[i]);
    fprintf(ffh_log, "\t%s=%s,%u,%u,%d,%" PRIu64 ",%u,%s", name, type,
            width, height, stride, *offset, bytes * height, hex);
    *offset += (uint64_t)bytes * height;
}
static void ffh_endrow(void)
{
    fputc('\n', ffh_log);
    ffh_flush();
}
static void ffh_arrays(x264_frame_t *f, int inv)
{
    if (!f->f_qp_offset || !f->f_qp_offset_aq || !f->i_inv_qscale_factor)
        ffh_fail("missing initialized AQ arrays");
    ffh_raw("offset", "f32", f->f_qp_offset, 6480, 1620, 1, 1);
    ffh_raw("offset_aq", "f32", f->f_qp_offset_aq, 6480, 1620, 1, 1);
    if (inv) ffh_raw("inv_qscale", "u16", f->i_inv_qscale_factor, 3240, 1620, 1, 1);
}
static void ffh_open(x264_t *h)
{
    const char *path = getenv("FFRAMES_HISTORY_TRACE");
    if (!path) return;
    if (!ffh_log) {
        const char *count = getenv("FFRAMES_HISTORY_LIFETIMES");
        if (!count || (strcmp(count, "2") && strcmp(count, "45")))
            ffh_fail("expected lifetimes must be explicitly 2 or 45");
        ffh_expected = !strcmp(count, "2") ? 2 : 45;
        int dir = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (dir < 0) ffh_fail("trace directory must already exist and not be a symlink");
        ffh_log = ffh_create(dir, "internal.tsv");
        ffh_pixels = ffh_create(dir, "pixels.raw");
        ffh_state = ffh_create(dir, "state.raw");
        if (close(dir)) ffh_fail("trace directory close failed");
        uint16_t endian = 1;
        fprintf(ffh_log, "schema\tffh-v1\t%s\t%u\n",
                *(unsigned char *)&endian ? "le" : "be", ffh_expected);
    }
    if (ffh_active || ffh_id >= ffh_expected || ffh_closes != ffh_id)
        ffh_fail("overlapping or excessive successful opens");
    ffh_gate(h);
    ffh_active = h; ++ffh_id; ffh_next = ffh_pending = 0;
    memset(ffh_seen, 0, sizeof ffh_seen); ffh_qpm_pending = -1;
    ffh8_enabled = 1;
    fprintf(ffh_log, "open\t%u\tcpu=%u\tthreads=%d\tthread_frames=%d"
        "\tlookahead_threads=%d\tsync_lookahead=%d\tsliced_threads=%d"
        "\tinterlaced=%d\tfake_interlaced=%d\tpic_struct=%d\tmb_interlaced=%d"
        "\tmb_width=%d\tmb_height=%d\tmb_stride=%d\tmb_count=%d\tlowres=%d"
        "\taq_mode=%d\taq_strength=%08" PRIx32 "\tmbtree=%d\tstat_read=%d\tstat_write=%d"
        "\tvbv_maxrate=%d\tvbv_buffer=%d\tstat_in_present=%d\tstat_out_present=%d\n",
        ffh_id, h->param.cpu, h->param.i_threads, h->i_thread_frames,
        h->param.i_lookahead_threads, h->param.i_sync_lookahead, h->param.b_sliced_threads,
        h->param.b_interlaced, h->param.b_fake_interlaced, h->param.b_pic_struct, h->mb.b_interlaced,
        h->mb.i_mb_width, h->mb.i_mb_height, h->mb.i_mb_stride, h->mb.i_mb_count,
        h->frames.b_have_lowres, h->param.rc.i_aq_mode, ffh_bits(&h->param.rc.f_aq_strength),
        h->param.rc.b_mb_tree, h->param.rc.b_stat_read, h->param.rc.b_stat_write,
        h->param.rc.i_vbv_max_bitrate, h->param.rc.i_vbv_buffer_size,
        !!h->param.rc.psz_stat_in, !!h->param.rc.psz_stat_out);
    ffh_flush();
}
static void ffh_entry(x264_t *h, x264_picture_t *p)
{
    if (!ffh8_enabled) return;
    ffh_check(h);
    if (ffh_pending || ffh_next >= 24 || p->i_pts != (int64_t)((ffh_id-1)*24+ffh_next)
        || p->img.i_csp != X264_CSP_I420 || p->img.i_plane != 3
        || p->param || p->prop.quant_offsets || p->prop.quant_offsets_free
        || p->prop.mb_info || p->prop.mb_info_free || p->extra_sei.num_payloads
        || p->extra_sei.payloads || p->extra_sei.sei_free)
        ffh_fail("unsupported entry identity/optional payload/order");
    ffh_mark(0, ffh_next); ffh_pending = 1;
    fprintf(ffh_log, "entry\t%u\t%u\t%" PRId64 "\tpic_type=%d\tqpplus1=%d\tpic_struct=%d",
            ffh_id, ffh_next, p->i_pts, p->i_type, p->i_qpplus1, p->i_pic_struct);
    ffh_raw("Y", "u8", p->img.plane[0], p->img.i_stride[0], 854, 480, 0);
    ffh_raw("U", "u8", p->img.plane[1], p->img.i_stride[1], 427, 240, 0);
    ffh_raw("V", "u8", p->img.plane[2], p->img.i_stride[2], 427, 240, 0);
    ffh_endrow();
}
static void ffh_coded(x264_t *h, x264_frame_t *f)
{
    if (!ffh8_enabled) return;
    ffh_check(h);
    /* i_frame is not assigned yet: use the pending submission ordinal. */
    if (ffh_pending != 1 || f->i_pts != (int64_t)((ffh_id-1)*24+ffh_next)
        || f->i_csp != X264_CSP_NV12 || f->i_plane != 2 || f->b_fdec
        || f->i_width[0] != 864 || f->i_lines[0] != 480
        || f->i_width[1] != 432 || f->i_lines[1] != 240)
        ffh_fail("unsupported post-mod16 coded geometry/identity");
    ffh_mark(1, ffh_next); ffh_pending = 2;
    fprintf(ffh_log, "coded\t%u\t%u\t%" PRId64, ffh_id, ffh_next, f->i_pts);
    ffh_raw("Y", "u8", f->plane[0], f->i_stride[0], 864, 480, 0);
    ffh_raw("UV", "u8", f->plane[1], f->i_stride[1], 864, 240, 0);
    ffh_endrow();
}
static void ffh_post_aq(x264_t *h, x264_frame_t *f)
{
    if (!ffh8_enabled) return;
    ffh_check(h);
    unsigned local = ffh_local(f);
    if (ffh_pending != 2 || local != ffh_next)
        ffh_fail("post-AQ does not match pending submission");
    ffh_mark(2, local); ffh_pending = 0; ++ffh_next;
    fprintf(ffh_log, "post_aq\t%u\t%u\t%" PRId64, ffh_id, local, f->i_pts);
    ffh_arrays(f, 1); ffh_endrow();
}
void ffh8_qpm_bytes(x264_t *h, const unsigned char raw[4], int b_vbv)
{
    if (!ffh8_enabled) return;
    ffh_check(h);
    unsigned local = ffh_local(h->fenc);
    if (b_vbv || ffh_pending || ffh_qpm_pending != -1
        || !(ffh_seen[2] & (1u << local)) || h->sh.b_mbaff)
        ffh_fail("unsupported qpm VBV/MBAFF or consumed ordering");
    ffh_mark(3, local); ffh_qpm_pending = (int)local;
    fprintf(ffh_log, "qpm\t%u\t%u\t%" PRId64 "\ttype=%d\tref=%d\tb_vbv=%d\tbits=%08" PRIx32,
            ffh_id, local, h->fenc->i_pts, h->fenc->i_type, h->fdec->b_kept_as_ref,
            b_vbv, ffh_bits(raw));
    ffh_raw("qpm", "f32", raw, 4, 1, 1, 1); ffh_endrow();
}
static void ffh_pre_slice(x264_t *h, int rc_global_qp)
{
    if (!ffh8_enabled) return;
    ffh_check(h);
    unsigned local = ffh_local(h->fenc);
    if (ffh_qpm_pending != (int)local || h->sh.b_mbaff)
        ffh_fail("pre-slice lacks matching actual qpm or uses MBAFF");
    ffh_mark(4, local); ffh_qpm_pending = -1;
    fprintf(ffh_log, "pre_slice\t%u\t%u\t%" PRId64
        "\ttype=%d\tref=%d\tslice_type=%d\tslice_init_qp=%d\trc_global_qp=%d",
        ffh_id, local, h->fenc->i_pts, h->fenc->i_type, h->fdec->b_kept_as_ref,
        h->sh.i_type, h->sh.i_qp, rc_global_qp);
    ffh_arrays(h->fenc, 0); ffh_endrow();
}
static void ffh_closing(x264_t *h)
{
    if (!ffh8_enabled) return;
    ffh_check(h);
    if (ffh_pending || ffh_qpm_pending != -1 || ffh_next != 24)
        ffh_fail("close with outstanding input/consumption");
    for (unsigned s = 0; s < 5; ++s)
        if (ffh_seen[s] != FFH_ALL) ffh_fail("close with incomplete phase coverage");
    ffh_is_closing = 1;
    fprintf(ffh_log, "closing\t%u\t24\t24\t24\t24\t24\n", ffh_id);
    ffh_flush();
}
static void ffh_closed(void)
{
    if (!ffh8_enabled) return;
    /* h has been freed: never pass, dereference or serialize it here. */
    if (!ffh_is_closing) ffh_fail("completion without native close begin");
    fprintf(ffh_log, "closed\t%u\n", ffh_id); ++ffh_closes;
    ffh_active = NULL; ffh_is_closing = 0;
    if (ffh_closes == ffh_expected) {
        fprintf(ffh_log, "terminal\t%u\t%u\t%" PRIu64 "\t%" PRIu64 "\n",
                ffh_closes, ffh_closes * 24, ffh_pixel_offset, ffh_state_offset);
        ffh_flush();
        /* Keep enabled: a forbidden additional open or hook must fail, not vanish. */
    } else ffh_flush();
}
#endif /* implementation part */
#else /* Non-8-bit objects emit no bridge declarations or definitions. */
#if defined(FFH_RATECONTROL_PART)
static void ffh_qpm(x264_t *h) { (void)h; }
#else
static void ffh_open(x264_t *h)
{
    (void)h;
    if (getenv("FFRAMES_HISTORY_TRACE")) {
        fprintf(stderr, "full-history observer: explicitly enabled non-8-bit encode\n");
        _exit(125);
    }
}
static void ffh_entry(x264_t *h, x264_picture_t *p) { (void)h; (void)p; }
static void ffh_coded(x264_t *h, x264_frame_t *f) { (void)h; (void)f; }
static void ffh_post_aq(x264_t *h, x264_frame_t *f) { (void)h; (void)f; }
static void ffh_pre_slice(x264_t *h, int qp) { (void)h; (void)qp; }
static void ffh_closing(x264_t *h) { (void)h; }
static void ffh_closed(void) {}
#endif
#endif
