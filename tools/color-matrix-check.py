#!/usr/bin/env python3
"""Identify the RGB->YCbCr matrix and range of the smoke outputs from their known source colors.

The plugin hands NVENC a BGRA texture and NVENC converts it to YCbCr itself; the stream does not say which
matrix it used. Both smoke tests draw known colors, so the mean Y/Cb/Cr of each decoded frame can be compared
with the means each candidate conversion would give (the conversions are affine, so plane means suffice and
4:2:0 subsampling does not matter):

  managed  tests/ManagedSmoke: frame f is solid (R, G, B) = (f / 30, 0.25, 0.5)            (managed-*.mp4)
  native   tests/NativeSmoke:  frame f pixel (x, y) is (R, G, B) = (7f, y + f, x + f) mod 256  (smoke-*.mp4)

Needs ffmpeg and ffprobe. Decoding keeps the stream's own pixel format, so no range or matrix conversion is
applied before the planes are read.
"""
import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

MATRICES = {'bt601': (0.299, 0.114), 'bt709': (0.2126, 0.0722)}
RANGES = ('limited', 'full')
LAYOUTS = {'yuv420p': 1.5, 'yuvj420p': 1.5, 'nv12': 1.5}


def to_ycbcr(rgb, matrix, color_range):
    """8-bit YCbCr for 8-bit RGB values (may be means) under one conversion."""
    kr, kb = MATRICES[matrix]
    r, g, b = (value / 255 for value in rgb)
    y = kr * r + (1 - kr - kb) * g + kb * b
    pb = (b - y) / (2 * (1 - kb))
    pr = (r - y) / (2 * (1 - kr))
    if color_range == 'limited':
        return 16 + 219 * y, 128 + 224 * pb, 128 + 224 * pr
    return 255 * y, 128 + 255 * pb, 128 + 255 * pr


def to_rgb(ycbcr, matrix, color_range):
    """What a player using this conversion shows for 8-bit YCbCr values (unclipped)."""
    kr, kb = MATRICES[matrix]
    y, cb, cr = ycbcr
    if color_range == 'limited':
        y, pb, pr = (y - 16) / 219, (cb - 128) / 224, (cr - 128) / 224
    else:
        y, pb, pr = y / 255, (cb - 128) / 255, (cr - 128) / 255
    r = y + 2 * (1 - kr) * pr
    b = y + 2 * (1 - kb) * pb
    g = (y - kr * r - kb * b) / (1 - kr - kb)
    return tuple(255 * value for value in (r, g, b))


def source_means(pattern, frame, width, height):
    """Mean source (R, G, B) of one frame of a smoke test."""
    if pattern == 'managed':
        return frame / 30 * 255, 0.25 * 255, 0.5 * 255
    if pattern == 'native':
        blue = sum((x + frame) % 256 for x in range(width)) / width
        green = sum((y + frame) % 256 for y in range(height)) / height
        return (7 * frame) % 256, green, blue
    raise ValueError(pattern)


def plane_means(data, width, height, pixel_format):
    luma = width * height
    chroma = (width // 2) * (height // 2)
    y = sum(data[:luma]) / luma
    if pixel_format == 'nv12':
        interleaved = data[luma:luma + 2 * chroma]
        return y, sum(interleaved[0::2]) / chroma, sum(interleaved[1::2]) / chroma
    return y, sum(data[luma:luma + chroma]) / chroma, sum(data[luma + chroma:luma + 2 * chroma]) / chroma


def fit(rows):
    """RMS error of each candidate over (source RGB, measured YCbCr) rows, best first."""
    results = []
    for matrix in MATRICES:
        for color_range in RANGES:
            squares = [0.0, 0.0, 0.0]
            for rgb, measured in rows:
                for index, (expected, actual) in enumerate(zip(to_ycbcr(rgb, matrix, color_range), measured)):
                    squares[index] += (expected - actual) ** 2
            channels = [math.sqrt(value / len(rows)) for value in squares]
            results.append(dict(Matrix=matrix, Range=color_range, Rms=math.sqrt(sum(value ** 2 for value in channels) / 3),
                                RmsY=channels[0], RmsCb=channels[1], RmsCr=channels[2]))
    return sorted(results, key=lambda row: row['Rms'])


def probe(path, ffprobe):
    fields = 'codec_name,profile,width,height,pix_fmt,color_range,color_space,color_transfer,color_primaries,nb_frames'
    output = subprocess.run([ffprobe, '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=' + fields,
                             '-of', 'json', str(path)], check=True, capture_output=True, text=True).stdout
    streams = json.loads(output).get('streams') or []
    if not streams:
        raise ValueError(f'{path}: no video stream')
    return streams[0]


def decode(path, stream, ffmpeg):
    width, height, pixel_format = int(stream['width']), int(stream['height']), stream['pix_fmt']
    if pixel_format not in LAYOUTS:
        raise ValueError(f'{path}: unsupported pixel format {pixel_format}')
    size = int(width * height * LAYOUTS[pixel_format])
    process = subprocess.Popen([ffmpeg, '-v', 'error', '-i', str(path), '-map', '0:v:0', '-f', 'rawvideo',
                                '-pix_fmt', pixel_format, '-'], stdout=subprocess.PIPE)
    try:
        while True:
            data = process.stdout.read(size)
            if len(data) < size:
                break
            yield plane_means(data, width, height, pixel_format)
    finally:
        process.stdout.close()
        if process.wait() != 0:
            raise RuntimeError(f'{path}: ffmpeg failed with {process.returncode}')


def analyze(path, pattern, ffmpeg='ffmpeg', ffprobe='ffprobe'):
    stream = probe(path, ffprobe)
    width, height = int(stream['width']), int(stream['height'])
    rows = [(source_means(pattern, frame, width, height), measured)
            for frame, measured in enumerate(decode(path, stream, ffmpeg))]
    if not rows:
        raise ValueError(f'{path}: no frames decoded')
    candidates = fit(rows)
    best = candidates[0]
    # The managed pattern's middle frame is a saturated color; show how a BT.709 player renders it.
    sample = source_means(pattern, min(15, len(rows) - 1), width, height)
    encoded = to_ycbcr(sample, best['Matrix'], best['Range'])
    shown = {f'{matrix}-{color_range}': [round(value, 1) for value in to_rgb(encoded, matrix, color_range)]
             for matrix in MATRICES for color_range in RANGES}
    signaled = {key: stream.get(key, 'unknown') for key in ('color_space', 'color_range', 'color_primaries', 'color_transfer')}
    return dict(File=str(path), Pattern=pattern, Codec=stream.get('codec_name'), PixelFormat=stream['pix_fmt'],
                Size=f'{width}x{height}', Frames=len(rows), Signaled=signaled, Candidates=candidates,
                Best=f"{best['Matrix']}-{best['Range']}", Margin=candidates[1]['Rms'] - best['Rms'],
                Sample=dict(SourceRgb=[round(value, 1) for value in sample],
                            EncodedYCbCr=[round(value, 1) for value in encoded], ShownAs=shown))


def default_inputs(dist):
    names = [('managed-audio-first.mp4', 'managed'), ('managed-threaded.mp4', 'managed'),
             ('smoke-h264.mp4', 'native'), ('smoke-hevc.mp4', 'native')]
    return [(dist / name, pattern) for name, pattern in names if (dist / name).exists()]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--managed', action='append', default=[], help='output of tests/ManagedSmoke (repeatable)')
    parser.add_argument('--native', action='append', default=[], help='output of tests/NativeSmoke (repeatable)')
    parser.add_argument('--dist', default='dist', help='folder searched when no file is given (default: dist)')
    parser.add_argument('--json', help='write the full results to this file')
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--ffprobe', default='ffprobe')
    args = parser.parse_args()
    inputs = [(Path(path), 'managed') for path in args.managed] + [(Path(path), 'native') for path in args.native]
    inputs = inputs or default_inputs(Path(args.dist))
    if not inputs:
        parser.error('no smoke output found; run build.ps1 -Smoke first or pass --managed/--native')
    results = []
    for path, pattern in inputs:
        result = analyze(path, pattern, args.ffmpeg, args.ffprobe)
        results.append(result)
        print(f"{result['File']} ({result['Codec']}, {result['PixelFormat']}, {result['Size']}, {result['Frames']} frames, {pattern})")
        print('  signaled: ' + ', '.join(f'{key}={value}' for key, value in result['Signaled'].items()))
        for row in result['Candidates']:
            print(f"  {row['Matrix']}-{row['Range']:<7}  rms {row['Rms']:6.2f}  (Y {row['RmsY']:.2f}, Cb {row['RmsCb']:.2f}, Cr {row['RmsCr']:.2f})")
        sample = result['Sample']
        print(f"  best: {result['Best']} (margin {result['Margin']:.2f}); source RGB {sample['SourceRgb']} -> YCbCr {sample['EncodedYCbCr']}")
        for name, rgb in sample['ShownAs'].items():
            print(f'    shown by a {name} player: RGB {rgb}')
    if args.json:
        Path(args.json).write_text(json.dumps(results, indent=2), encoding='utf-8')
    return 0


if __name__ == '__main__':
    sys.exit(main())
