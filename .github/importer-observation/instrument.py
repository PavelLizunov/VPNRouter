"""Patch only a hash-bound disposable r3108 x264 source tree."""
import argparse
import hashlib
import json
from pathlib import Path

SOURCE_HASHES = {
    'encoder/encoder.c': '5bdac6592b556782a05b3fee0c5bfca4daffc9a372d06af1cc70b1f06406e9d6',
    'common/frame.c': 'daca9eb48564ad960249ba079c980ecd206febe0ae9edb891b5d5dadef802262',
    'common/frame.h': '3bebbadda7bfdfd2017c708bde2f4556daad8b6fc8f44fb1bdab5616b13722fd',
}
INCLUDE = '#include "common/common.h"\n'
ENTRY = '        if( x264_frame_copy_picture( h, fenc, pic_in ) < 0 )\n'
CODED = '        fenc->i_frame = h->frames.i_input++;\n'


def patch_encoder(data):
    text = data.decode('utf-8')
    for anchor in (INCLUDE, ENTRY, CODED):
        if text.count(anchor) != 1:
            raise ValueError('Missing or ambiguous source anchor')
    if text.index(ENTRY) >= text.index(CODED):
        raise ValueError('Unexpected importer order')
    return (text.replace(INCLUDE, INCLUDE + '#include "importer-observer.h"\n')
            .replace(ENTRY, '        fframes_import_entry( h, pic_in );\n' + ENTRY)
            .replace(CODED, '        fframes_import_coded( h, fenc );\n\n' + CODED)).encode('utf-8')


def instrument(source, header):
    for name, expected in SOURCE_HASHES.items():
        if hashlib.sha256((source/name).read_bytes()).hexdigest() != expected:
            raise ValueError(f'Unsupported native source: {name}')
    target = source/'encoder/encoder.c'
    patched = patch_encoder(target.read_bytes())
    observer = header.read_bytes()
    with (source/'encoder/importer-observer.h').open('xb') as stream:
        stream.write(observer)
    target.write_bytes(patched)
    return {'original_source_sha256': SOURCE_HASHES,
            'patched_encoder_sha256': hashlib.sha256(patched).hexdigest(),
            'observer_header_sha256': hashlib.sha256(observer).hexdigest(),
            'historical_binary': False, 'scope': 'disposable diagnostic source only'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--header', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(instrument(args.source, args.header), indent=2))
