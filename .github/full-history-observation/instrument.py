"""Byte-bound r3108 disposable-tree instrumentation (never build or run native code)."""
import argparse
import hashlib
import json
from pathlib import Path
import tarfile

ARCHIVE_SHA256 = '41606cb8e788a7f8c4514290646d4ba5c7bc68d9e1ccd1a73f446a90546913eb'
ARCHIVE_NAME = 'x264_0.164.3108+git31e19f9.orig.tar.gz'
PREFIX = 'x264-0.164.3108+git31e19f9/'
SOURCE_HASHES = {
    'encoder/encoder.c': '5bdac6592b556782a05b3fee0c5bfca4daffc9a372d06af1cc70b1f06406e9d6',
    'encoder/ratecontrol.c': 'c03cb0025d983ceee142644ba4328da5c59f53f2bd0f83bd6225dbaa6a31f87c',
    'common/frame.c': 'daca9eb48564ad960249ba079c980ecd206febe0ae9edb891b5d5dadef802262',
    'common/frame.h': '3bebbadda7bfdfd2017c708bde2f4556daad8b6fc8f44fb1bdab5616b13722fd',
    'common/common.h': '7b51ceebe2361cbb30227c9f4fdec088cdb3997226c91dc39edc709c4c2933ab',
    'common/macroblock.h': '227518e7a96d62383d607f72f03f0513cb5ca91fd5d465eb4b7437aa5ed84726',
    'encoder/lookahead.c': 'df8323cef7a7a17af4169c7896ccb991c71f9e5a7256bdf0869bd0f2e0b11e66',
    'x264.h': 'adfaae08c4c22ecccc5f26d4879c79084ee779cc83e2aa99150541d8ddd7cd3e',
    'common/macroblock.c': 'f2ad587f7c4d32900c98118a3bfb8cbe40fdb4420fb66666d2bc141753cdd80f',
    'encoder/slicetype.c': '15cea2a5022ff01b4a92f5061c5131cc84ab98c9408ac578068cd7fc10f84d09',
    'encoder/analyse.c': '4f001999b9ba9fda55b6b2818ceba151ea65ab67e8da0dc26b082ef1b579da96',
    'encoder/ratecontrol.h': '98b59316641bd3367e8c79082ef0b61b3c359fad185234a80d823417809429b4',
    'encoder/api.c': '35735d4853a5d10188764be7526590cf63621bb8639eca036bf0ab602220b8d8',
    'common/base.h': 'bc78e03b833985f10a278f5dcec3da7ee800e38218e7ad9d53b4bfe666ba4baa',
    'common/cpu.h': '01b8a8098fd1955a33cda4cdf609160f8f42119982a15a6592812162d38251e5',
    'Makefile': '68cd026834dd615820ec4251f02f0be2b90ea8e944c3bed01b0be6cbc3da11fd',
    'configure': '15627479c6dbe66d5e9b6b89cf23b5279eb4906af05b6f6911194770cd7c2009',
}
INCLUDE = '#include "common/common.h"\n'
OPEN = '    return h;\nfail:\n    x264_free( h );\n    return NULL;\n'
ENTRY = '        if( x264_frame_copy_picture( h, fenc, pic_in ) < 0 )\n'
CODED = '        fenc->i_frame = h->frames.i_input++;\n'
AQ = '        if( pic_in->prop.quant_offsets_free )\n'
SLICE = '    /* Write frame */\n    h->i_threadslice_start = 0;\n'
CLOSING = 'void    x264_encoder_close  ( x264_t *h )\n{\n'
CLOSED = '#if HAVE_OPENCL\n    x264_opencl_close_library( ocl );\n#endif\n}\n'
RC_INCLUDE = '};\n\n\nstatic int parse_zones( x264_t *h );\n'
QPM = 'int x264_ratecontrol_qp( x264_t *h )\n{\n    x264_emms();\n'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def patch_member(data, anchors, replacements):
    """Full-string uniqueness/order checks remain independent of the hash check."""
    text = data.decode('utf-8')
    if any(text.count(a) != 1 for a in anchors):
        raise ValueError('Missing or duplicate full-string source anchor')
    positions = [text.index(a) for a in anchors]
    if positions != sorted(positions):
        raise ValueError('Shifted source anchor order')
    for old, new in replacements:
        text = text.replace(old, new, 1)
    return text.encode('utf-8')


def patch_encoder(data):
    return patch_member(data, (INCLUDE, OPEN, ENTRY, CODED, AQ, SLICE, CLOSING, CLOSED), (
        (INCLUDE, INCLUDE + '#define FFH_ENCODER_PART\n#include "full-history-observer.h"\n'),
        (OPEN, '    ffh_open( h );\n' + OPEN),
        (ENTRY, '        ffh_entry( h, pic_in );\n' + ENTRY),
        (CODED, '        ffh_coded( h, fenc );\n' + CODED),
        (AQ, '        ffh_post_aq( h, fenc );\n' + AQ),
        (SLICE, '    ffh_pre_slice( h, i_global_qp );\n' + SLICE),
        (CLOSING, CLOSING + '    ffh_closing( h );\n'),
        (CLOSED, CLOSED[:-2] + '    ffh_closed();\n}\n'),
    ))


def patch_ratecontrol(data):
    return patch_member(data, (RC_INCLUDE, QPM), (
        (RC_INCLUDE, '};\n\n#define FFH_RATECONTROL_PART\n#include "full-history-observer.h"\n\n\nstatic int parse_zones( x264_t *h );\n'),
        (QPM, QPM + '    ffh_qpm( h );\n'),
    ))


def instrument(source: Path, header: Path, archive: Path | None = None):
    """Validate the complete orig closure before the first disposable-source write.

    The caller must supply a fresh disposable extraction, not a checkout. No
    downstream Debian patch is accepted. Archive defaults to the tree's sibling.
    """
    source, header = Path(source), Path(header)
    archive = Path(archive) if archive is not None else source.parent / ARCHIVE_NAME
    if archive.is_symlink() or sha(archive.read_bytes()) != ARCHIVE_SHA256:
        raise ValueError('Unsupported orig source archive')
    originals = {}
    with tarfile.open(archive, 'r:gz') as tar:
        members = tar.getmembers()
        for name, expected in SOURCE_HASHES.items():
            found = [m for m in members if m.name == PREFIX + name]
            if len(found) != 1 or not found[0].isfile():
                raise ValueError(f'Invalid archive member: {name}')
            data = tar.extractfile(found[0]).read()
            target = source / name
            if target.is_symlink() or not target.is_file():
                raise ValueError(f'Nonregular native source: {name}')
            # Reject symlink parents as well as symlink leaves.
            if any(p.is_symlink() for p in (target.absolute(), *target.absolute().parents)):
                raise ValueError(f'Symlink native source path: {name}')
            loose = target.read_bytes()
            if sha(data) != expected or loose != data:
                raise ValueError(f'Unsupported native source: {name}')
            originals[name] = loose
    patched = {'encoder/encoder.c': patch_encoder(originals['encoder/encoder.c']),
               'encoder/ratecontrol.c': patch_ratecontrol(originals['encoder/ratecontrol.c'])}
    if header.is_symlink() or not header.is_file():
        raise ValueError('Nonregular observer header')
    observer = header.read_bytes()
    destination = source / 'encoder/full-history-observer.h'
    if destination.exists() or destination.is_symlink():
        raise ValueError('Observer header destination already exists')
    with destination.open('xb') as stream:
        stream.write(observer)
    for name, data in patched.items():
        (source / name).write_bytes(data)
    return {'schema': 'ffh-source-v1', 'archive_sha256': ARCHIVE_SHA256,
            'original_source_sha256': SOURCE_HASHES,
            'patched_source_sha256': {name: sha(data) for name, data in patched.items()},
            'observer_header_sha256': sha(observer), 'historical_binary': False,
            'scope': 'disposable orig source only; native validation deferred to CI'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--header', type=Path, required=True)
    parser.add_argument('--archive', type=Path)
    args = parser.parse_args()
    print(json.dumps(instrument(args.source, args.header, args.archive), indent=2))
