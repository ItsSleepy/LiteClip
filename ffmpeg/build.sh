#!/usr/bin/env bash
# Builds the slim ffmpeg.exe / ffprobe.exe LiteClip ships with.
# Runs in MSYS2 UCRT64 (see .github/workflows/build.yml). Only what LiteClip uses is enabled,
# which keeps the binary around 15 MB instead of 165 MB.
set -euo pipefail
FF_BRANCH="${FF_BRANCH:-release/8.1}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-$HERE/../build/ffmpeg}"
OUT="${OUT:-$HERE/../build/ffmpeg-out}"
mkdir -p "$WORK" "$OUT"
cd "$WORK"
[ -d src ] || git clone --depth 1 -b "$FF_BRANCH" https://github.com/FFmpeg/FFmpeg.git src
cd src

# LiteClip patch: keep gfxcapture's absolute capture time (QPC, 100 ns units) instead of
# rebasing to the first frame. The app stamps its own audio with the same clock, so
# video and audio line up exactly, with no drift over long sessions.
f=libavfilter/vsrc_gfxcapture_winrt.cpp
if ! grep -q "LiteClip:" "$f"; then
  sed -i 's|^\(\s*\)frame->pts -= ctx->first_pts;|\1/* LiteClip: keep absolute QPC time so in-app audio aligns exactly */|' "$f"
fi
grep -q "LiteClip:" "$f" || { echo "gfxcapture patch did not apply"; exit 1; }

# LiteClip patch: some drivers (seen on Intel) refuse an NV12 texture array that is both a
# render target and a video-encoder input (E_INVALIDARG). Retry as a plain render target.
f=libavfilter/vf_scale_d3d11.c
if ! grep -q "LiteClip:" "$f"; then
  python3 - "$f" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
old = """    ret = av_hwframe_ctx_init(s->hw_frames_ctx_out);
    if (ret < 0) {
        av_buffer_unref(&s->hw_frames_ctx_out);
        return ret;
    }"""
new = """    ret = av_hwframe_ctx_init(s->hw_frames_ctx_out);
    if (ret < 0) {
        /* LiteClip: retry without D3D11_BIND_VIDEO_ENCODER */
        av_buffer_unref(&s->hw_frames_ctx_out);
        s->hw_frames_ctx_out = av_hwframe_ctx_alloc(s->hw_device_ctx);
        if (!s->hw_frames_ctx_out)
            return AVERROR(ENOMEM);
        frames_ctx = (AVHWFramesContext *)s->hw_frames_ctx_out->data;
        frames_ctx->format = AV_PIX_FMT_D3D11;
        frames_ctx->sw_format = s->format;
        frames_ctx->width = s->width;
        frames_ctx->height = s->height;
        frames_ctx->initial_pool_size = 10 + FFMAX(ctx->extra_hw_frames, 0);
        frames_hwctx = frames_ctx->hwctx;
        frames_hwctx->MiscFlags = 0;
        frames_hwctx->BindFlags = D3D11_BIND_RENDER_TARGET;
        av_log(ctx, AV_LOG_VERBOSE, "retrying output frames as a plain render target\\n");
        ret = av_hwframe_ctx_init(s->hw_frames_ctx_out);
        if (ret < 0) {
            av_buffer_unref(&s->hw_frames_ctx_out);
            return ret;
        }
    }"""
assert old in s, "scale_d3d11 anchor not found"
s = s.replace(old, new, 1)
open(p, "w").write(s)
PY
fi
grep -q "LiteClip:" "$f" || { echo "scale_d3d11 patch did not apply"; exit 1; }

list() { local IFS=,; echo "$*"; }
FILTERS=(gfxcapture ddagrab fps setpts asetpts settb asettb amix aresample aformat volume
  anull null format hwdownload hwupload hwmap scale scale_d3d11 split asplit atrim trim
  thumbnail pan amerge crop pad palettegen paletteuse anullsrc color testsrc2 concat
  showinfo copy acopy setparams)
ENCODERS=(h264_nvenc hevc_nvenc av1_nvenc h264_amf hevc_amf av1_amf h264_mf hevc_mf
  aac mjpeg png gif pcm_s16le pcm_f32le rawvideo)
DECODERS=(h264 hevc av1 aac pcm_f32le pcm_s16le mjpeg png gif rawvideo wrapped_avframe)
HWACCELS=(h264_d3d11va h264_d3d11va2 hevc_d3d11va hevc_d3d11va2 av1_d3d11va av1_d3d11va2)
DEMUXERS=(mpegts mov concat pcm_f32le pcm_s16le image2 wav aac h264 hevc gif)
MUXERS=(mpegts mp4 mov ipod segment image2 null gif wav adts matroska)
PARSERS=(h264 hevc aac av1 mjpeg png gif)
BSFS=(h264_mp4toannexb hevc_mp4toannexb aac_adtstoasc extract_extradata null)

./configure \
  --prefix="$OUT" --arch=x86_64 --target-os=mingw32 \
  --pkg-config-flags=--static --extra-ldflags=-static --extra-cflags=-O2 \
  --enable-static --disable-shared --disable-debug --disable-doc --disable-ffplay \
  --disable-network --disable-autodetect --disable-everything \
  --enable-w32threads --enable-d3d11va --enable-ffnvcodec --enable-nvenc \
  --enable-amf --enable-mediafoundation \
  --enable-filter="$(list "${FILTERS[@]}")" \
  --enable-encoder="$(list "${ENCODERS[@]}")" \
  --enable-decoder="$(list "${DECODERS[@]}")" \
  --enable-hwaccel="$(list "${HWACCELS[@]}")" \
  --enable-demuxer="$(list "${DEMUXERS[@]}")" \
  --enable-muxer="$(list "${MUXERS[@]}")" \
  --enable-parser="$(list "${PARSERS[@]}")" \
  --enable-bsf="$(list "${BSFS[@]}")" \
  --enable-protocol=file,pipe,concat \
  --enable-indev=lavfi \
  || { tail -60 ffbuild/config.log; exit 1; }

# Fail loudly if a must-have piece silently dropped out (missing headers etc.)
for need in GFXCAPTURE_FILTER SCALE_D3D11_FILTER H264_NVENC_ENCODER H264_AMF_ENCODER H264_MF_ENCODER; do
  cat config.h config_components.h 2>/dev/null | grep -q "^#define CONFIG_$need 1" || { echo "missing: $need"; exit 1; }
done

make -j"$(nproc)"
make install
strip "$OUT/bin/ffmpeg.exe" "$OUT/bin/ffprobe.exe"
ls -la "$OUT/bin"
