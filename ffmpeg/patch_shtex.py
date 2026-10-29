"""LiteClip patch: let gfxcapture read frames from a game-capture hook instead of Windows Graphics Capture.

With `shtex=<handle>` the filter opens the shared D3D11 texture a capture hook (OBS's
graphics-hook) copies every game frame into, and samples it at a steady `max_framerate`.
Windows Graphics Capture is skipped entirely, so a game that keeps the GPU at 100% (or runs in
exclusive fullscreen) no longer starves the recording of frames.

Usage: python3 patch_shtex.py <ffmpeg source dir>
"""
import sys, os

root = sys.argv[1]
def patch(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding="utf-8").read()
    if "LiteClip shtex" in s:
        return
    for old, new in pairs:
        assert old in s, f"{rel}: anchor not found:\n{old}"
        s = s.replace(old, new, 1)
    open(p, "w", encoding="utf-8").write(s)

patch("libavfilter/vsrc_gfxcapture.h", [(
    "    uint64_t user_hwnd;\n",
    "    uint64_t user_hwnd;\n    uint64_t shtex; /* LiteClip shtex: shared texture handle from a game-capture hook */\n",
)])

patch("libavfilter/vsrc_gfxcapture.c", [(
    '    { "hmonitor",',
    '    { "shtex",           "read from a game-capture hook\'s shared texture (LiteClip shtex)",\n'
    '                                                              OFFSET(shtex),          AV_OPT_TYPE_UINT64,     { .i64 = 0 },      0, UINT64_MAX, FLAGS },\n'
    '    { "hmonitor",',
)])

patch("libavfilter/vsrc_gfxcapture_winrt.cpp", [
    # state
    ("    SizeInt32 cap_size { 0, 0 };\n    RECT client_area_offsets { 0, 0, 0, 0 };\n};",
     "    SizeInt32 cap_size { 0, 0 };\n    RECT client_area_offsets { 0, 0, 0, 0 };\n\n"
     "    /* LiteClip shtex */\n"
     "    ComPtr<ID3D11Texture2D> shtex;\n"
     "    int64_t shtex_next { 0 };\n"
     "    HANDLE shtex_timer { nullptr };\n};"),

    # setup: open the shared texture instead of creating a capture session
    ("static int wgc_setup_gfxcapture_capture(AVFilterContext *avctx)\n{\n"
     "    GfxCaptureContext *cctx = CCTX(avctx->priv);\n"
     "    GfxCaptureContextCpp *ctx = cctx->ctx;\n"
     "    std::unique_ptr<GfxCaptureContextWgc> &wgctx = ctx->wgc;\n"
     "    HRESULT hr;\n    int ret;\n",
     "static int wgc_setup_shtex(AVFilterContext *avctx)\n{\n"
     "    GfxCaptureContext *cctx = CCTX(avctx->priv);\n"
     "    GfxCaptureContextCpp *ctx = cctx->ctx;\n"
     "    std::unique_ptr<GfxCaptureContextWgc> &wgctx = ctx->wgc;\n"
     "    ComPtr<ID3D11Device> d3d11_device = ctx->device_hwctx->device;\n"
     "    ComPtr<ID3D10Multithread> d3d10_multithread;\n"
     "    CHECK_HR_RET(d3d11_device.As(&d3d10_multithread));\n"
     "    d3d10_multithread->SetMultithreadProtected(TRUE);\n"
     "    HRESULT hr = d3d11_device->OpenSharedResource((HANDLE)(uintptr_t)cctx->shtex, IID_PPV_ARGS(&wgctx->shtex));\n"
     "    if (FAILED(hr) || !wgctx->shtex) {\n"
     "        av_log(avctx, AV_LOG_ERROR, \"Failed to open the game hook's shared texture (0x%08lX)\\n\", hr);\n"
     "        return AVERROR_EXTERNAL;\n"
     "    }\n"
     "    D3D11_TEXTURE2D_DESC desc;\n"
     "    wgctx->shtex->GetDesc(&desc);\n"
     "    wgctx->cap_size.Width = desc.Width;\n"
     "    wgctx->cap_size.Height = desc.Height;\n"
     "    wgctx->client_area_offsets = RECT { 0, 0, 0, 0 };\n"
     "    wgctx->shtex_timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, TIMER_ALL_ACCESS);\n"
     "    if (!wgctx->shtex_timer)\n"
     "        wgctx->shtex_timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);\n"
     "    av_log(avctx, AV_LOG_INFO, \"Reading game hook texture %ux%u format %d\\n\", desc.Width, desc.Height, (int)desc.Format);\n"
     "    return 0;\n}\n\n"
     "static int wgc_setup_gfxcapture_capture(AVFilterContext *avctx)\n{\n"
     "    GfxCaptureContext *cctx = CCTX(avctx->priv);\n"
     "    GfxCaptureContextCpp *ctx = cctx->ctx;\n"
     "    std::unique_ptr<GfxCaptureContextWgc> &wgctx = ctx->wgc;\n"
     "    HRESULT hr;\n    int ret;\n"
     "    if (cctx->shtex)\n        return wgc_setup_shtex(avctx);\n"),

    # the hook texture is typeless (so the game can alias sRGB); view it as plain UNORM
    ("    CHECK_HR_RET(dev->CreateShaderResourceView(src_tex.Get(), nullptr, &srv));",
     "    D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc = {};\n"
     "    srv_desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;\n"
     "    srv_desc.Texture2D.MipLevels = 1;\n"
     "    switch (src_tex_desc.Format) {\n"
     "    case DXGI_FORMAT_B8G8R8A8_TYPELESS: srv_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; break;\n"
     "    case DXGI_FORMAT_R8G8B8A8_TYPELESS: srv_desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; break;\n"
     "    case DXGI_FORMAT_R10G10B10A2_TYPELESS: srv_desc.Format = DXGI_FORMAT_R10G10B10A2_UNORM; break;\n"
     "    case DXGI_FORMAT_R16G16B16A16_TYPELESS: srv_desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; break;\n"
     "    default: srv_desc.Format = src_tex_desc.Format; break;\n"
     "    }\n"
     "    CHECK_HR_RET(dev->CreateShaderResourceView(src_tex.Get(), &srv_desc, &srv));"),

    # game back buffers carry junk alpha; never un-premultiply them
    ("        cb_u[8] = !cctx->premult_alpha; // to_unpremult",
     "        cb_u[8] = !cctx->premult_alpha && !cctx->shtex; // to_unpremult"),

    # frames: pace ourselves at max_framerate and sample the latest hooked frame
    ("    AVFrame *frame = nullptr;\n\n    ret = run_on_wgc_thread(avctx, [&]() {\n",
     "    AVFrame *frame = nullptr;\n\n"
     "    if (cctx->shtex) {\n"
     "        std::unique_ptr<GfxCaptureContextWgc> &wgctx = ctx->wgc;\n"
     "        LARGE_INTEGER qf, qc;\n"
     "        QueryPerformanceFrequency(&qf);\n"
     "        auto now100 = [&]() { QueryPerformanceCounter(&qc); return (int64_t)av_rescale(qc.QuadPart, TIMESPAN_RES64, qf.QuadPart); };\n"
     "        int64_t interval = av_rescale_q(1, av_inv_q(cctx->frame_rate), AVRational{1, TIMESPAN_RES});\n"
     "        int64_t now = now100();\n"
     "        if (!wgctx->shtex_next || now - wgctx->shtex_next > 4 * interval)\n"
     "            wgctx->shtex_next = now;\n"
     "        while (now < wgctx->shtex_next) {\n"
     "            int64_t left = wgctx->shtex_next - now;\n"
     "            if (wgctx->shtex_timer && left > 12000) {\n"
     "                LARGE_INTEGER due; due.QuadPart = -(left - 10000);\n"
     "                if (SetWaitableTimer(wgctx->shtex_timer, &due, 0, nullptr, nullptr, FALSE))\n"
     "                    WaitForSingleObject(wgctx->shtex_timer, 100);\n"
     "            } else {\n"
     "                SwitchToThread();\n"
     "            }\n"
     "            now = now100();\n"
     "        }\n"
     "        int64_t pts = wgctx->shtex_next;\n"
     "        wgctx->shtex_next += interval;\n"
     "        ret = run_on_wgc_thread(avctx, [&]() {\n"
     "            frame = ff_get_video_buffer(outlink, cctx->canvas_width, cctx->canvas_height);\n"
     "            if (!frame)\n"
     "                return AVERROR(ENOMEM);\n"
     "            frame->pts = pts;\n"
     "            return render_capture_to_frame(avctx, frame, wgctx->shtex);\n"
     "        });\n"
     "    } else\n"
     "    ret = run_on_wgc_thread(avctx, [&]() {\n"),
])

# the shared texture and timer are released with the rest of the WGC state
patch_cleanup = ("    wgctx->frame_pool.Reset();\n",
                 "    wgctx->frame_pool.Reset();\n"
                 "    wgctx->shtex.Reset(); /* LiteClip shtex */\n"
                 "    if (wgctx->shtex_timer) { CloseHandle(wgctx->shtex_timer); wgctx->shtex_timer = nullptr; }\n")
p = os.path.join(root, "libavfilter/vsrc_gfxcapture_winrt.cpp")
s = open(p, encoding="utf-8").read()
if "wgctx->shtex.Reset()" not in s:
    assert patch_cleanup[0] in s, "cleanup anchor not found"
    s = s.replace(patch_cleanup[0], patch_cleanup[1], 1)
    open(p, "w", encoding="utf-8").write(s)
print("shtex patch applied")
