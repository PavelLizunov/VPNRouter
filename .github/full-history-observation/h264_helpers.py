"""Saved, bounded H.264 header definitions; no historical main or media execution.

Definitions Bits through self_tests are reused from inspect_idr_headers.py
(SHA256 4b3aad13b4e374352e7bf301b827a5206ed70e3add1c9c075bff5db6616828a1).
They deliberately support only this film's progressive High/8-bit/4:2:0 syntax.
"""
import hashlib

if not __debug__:
    raise RuntimeError('Bounded H.264 assertions require Python without -O')


class Bits:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def u(self, width):
        assert 0 <= width <= 32 and self.pos + width <= len(self.data) * 8, 'bit range'
        value = 0
        for _ in range(width):
            value = value * 2 + ((self.data[self.pos // 8] >> (7 - self.pos % 8)) & 1)
            self.pos += 1
        return value

    def ue(self):
        zeros = 0
        while self.u(1) == 0:
            zeros += 1
            assert zeros <= 31, 'Exp-Golomb bound'
        return (1 << zeros) - 1 + self.u(zeros)

    def se(self):
        code = self.ue()
        return (code + 1) // 2 if code & 1 else -(code // 2)


def rbsp(nal):
    assert nal and nal[0] & 0x80 == 0
    result = bytearray()
    zeros = 0
    for i, value in enumerate(nal[1:]):
        if zeros == 2 and value == 3:
            assert i + 2 < len(nal) and nal[i + 2] <= 3, 'invalid escape'
            zeros = 0
            continue
        result.append(value)
        zeros = zeros + 1 if value == 0 else 0
    return bytes(result)


def parameter_sets(data):
    # Bounded unique avcC box occurrence in this known MP4 fixture, not a general demuxer.
    hits = []
    start = 0
    while (index := data.find(b'avcC', start)) >= 0:
        start = index + 4
        if index >= 4:
            length = int.from_bytes(data[index - 4:index], 'big')
            if 15 <= length <= 65536 and index - 4 + length <= len(data):
                hits.append(data[index + 4:index - 4 + length])
    assert len(hits) == 1, 'ambiguous avcC'
    avcc, = hits
    assert avcc[0] == 1 and avcc[4] & 3 == 3
    cursor = 6
    sets = []
    for count in (avcc[5] & 31,):
        assert count == 1
        for _ in range(count):
            size = int.from_bytes(avcc[cursor:cursor + 2], 'big')
            cursor += 2
            assert size > 0 and cursor + size <= len(avcc)
            sets.append(avcc[cursor:cursor + size])
            cursor += size
    assert cursor < len(avcc) and avcc[cursor] == 1
    cursor += 1
    size = int.from_bytes(avcc[cursor:cursor + 2], 'big')
    cursor += 2
    assert size > 0 and cursor + size <= len(avcc)
    sets.append(avcc[cursor:cursor + size])
    sps, pps = sets
    assert sps[0] & 31 == 7 and pps[0] & 31 == 8
    b = Bits(rbsp(sps))
    profile, constraints, level, sps_id = b.u(8), b.u(8), b.u(8), b.ue()
    assert profile == 100 and sps_id == 0
    assert b.ue() == 1 and b.ue() == 0 and b.ue() == 0, 'only 8-bit 4:2:0'
    assert b.u(1) == 0 and b.u(1) == 0, 'no transform bypass/scaling matrix'
    frame_num_bits = b.ue() + 4
    assert b.ue() == 0, 'only POC type 0'
    poc_bits = b.ue() + 4
    refs, gaps = b.ue(), b.u(1)
    width_mbs, height_units = b.ue() + 1, b.ue() + 1
    assert b.u(1) == 1, 'only progressive'
    direct = b.u(1)
    crop = [b.ue() for _ in range(4)] if b.u(1) else [0] * 4
    assert width_mbs * 16 - 2 * (crop[0] + crop[1]) == 854
    assert height_units * 16 - 2 * (crop[2] + crop[3]) == 480
    b = Bits(rbsp(pps))
    assert b.ue() == 0 and b.ue() == sps_id
    entropy, bottom_poc, groups = b.u(1), b.u(1), b.ue()
    assert entropy == 1 and bottom_poc == 0 and groups == 0
    defaults = [b.ue(), b.ue(), b.u(1), b.u(2)]
    initial_qp = 26 + b.se()
    initial_qs, chroma_qp = b.se(), b.se()
    deblock, constrained, redundant = b.u(1), b.u(1), b.u(1)
    assert deblock == 1 and redundant == 0
    return {'profile': profile, 'constraints': constraints, 'level': level,
            'frame_num_bits': frame_num_bits, 'poc_bits': poc_bits, 'refs': refs,
            'gaps': gaps, 'direct': direct, 'crop': crop, 'defaults': defaults,
            'initial_qp': initial_qp, 'initial_qs': initial_qs, 'chroma_qp': chroma_qp,
            'constrained': constrained, 'sps_sha256': hashlib.sha256(sps).hexdigest(),
            'pps_sha256': hashlib.sha256(pps).hexdigest()}


def idr_header(nal, parameters):
    assert nal[0] & 31 == 5 and nal[0] >> 5 & 3, 'only reference IDR'
    raw = rbsp(nal)
    b = Bits(raw)
    first_mb, slice_type, pps_id = b.ue(), b.ue(), b.ue()
    assert first_mb == 0 and slice_type % 5 == 2 and pps_id == 0, 'only I slice'
    frame_num, idr_id = b.u(parameters['frame_num_bits']), b.ue()
    poc = b.u(parameters['poc_bits'])
    no_output, long_term, qp_delta = b.u(1), b.u(1), b.se()
    deblock = b.ue()
    assert deblock in (0, 1, 2)
    offsets = [b.se(), b.se()] if deblock != 1 else []
    syntax_bits = b.pos
    while b.pos % 8:
        assert b.u(1) == 1, 'CABAC alignment'
    return {'nal_ref_idc': nal[0] >> 5 & 3, 'slice_type': slice_type,
            'frame_num': frame_num, 'idr_id': idr_id, 'poc': poc,
            'no_output': no_output, 'long_term': long_term, 'qp_delta': qp_delta,
            'slice_qp': parameters['initial_qp'] + qp_delta, 'deblock': deblock,
            'offsets': offsets, 'syntax_bits': syntax_bits, 'body_byte': b.pos // 8}, raw


def self_tests():
    assert Bits(b'\x80').ue() == 0
    assert Bits(b'\x40').ue() == 1 and Bits(b'\x60').ue() == 2
    assert Bits(b'\x40').se() == 1 and Bits(b'\x60').se() == -1
    assert rbsp(b'\x65\x00\x00\x03\x01') == b'\x00\x00\x01'
    for operation in (lambda: Bits(b'').u(1), lambda: Bits(b'\x00').ue(),
                      lambda: Bits(b'\x00' * 5).ue(), lambda: rbsp(b'\x65\x00\x00\x03\x04')):
        try:
            operation()
        except AssertionError:
            continue
        raise AssertionError('negative fixture accepted')
