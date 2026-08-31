/*
 * KiwiNativeCameraPlugin_RECONSTRUCTED_CANDIDATE.cpp
 * Static-analysis reconstruction candidate, 2026-08-29.
 *
 * IMPORTANT:
 *   - This is NOT recovered original source and is NOT bit-identical to the DLL.
 *   - It has not been compiled, linked, loaded by Unity, or runtime-tested.
 *   - It targets the proven Production Path B invariants (mode 9) only.
 *   - Do not copy it into the Unity project or replace Production without the
 *     review/build/runtime gates listed in REPORT.md.
 *
 * Required headers: Windows 10/11 SDK and Unity 6000.0.80f1 PluginAPI.
 * Language: MSVC C++17 or later.
 * Libraries: mf, mfplat, mfreadwrite, mfuuid, ole32, d3d11, d3d12, dxgi, d3dcompiler.
 */

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <d3d10_1.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <d3d12compatibility.h>
#include <d3dcompiler.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <iterator>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

using Microsoft::WRL::ComPtr;

namespace kiwi_camera_candidate {

constexpr int kSlotCount = 3;
constexpr int kPresentEventBase = 0x4B7700;
constexpr int kProductionMode = 9;
constexpr int kSystemMemoryTransport = 1;

static int64_t QpcNow() noexcept {
    LARGE_INTEGER v{};
    QueryPerformanceCounter(&v);
    return v.QuadPart;
}

static int64_t QpcFrequency() noexcept {
    static const int64_t f = [] {
        LARGE_INTEGER v{};
        QueryPerformanceFrequency(&v);
        return v.QuadPart;
    }();
    return f;
}

static uint64_t QpcToMicroseconds(int64_t ticks) noexcept {
    const int64_t f = QpcFrequency();
    return ticks > 0 && f > 0
        ? static_cast<uint64_t>((ticks * 1000000LL) / f)
        : 0ULL;
}

struct Telemetry final {
    std::atomic<uint64_t> allSlotsBusyDrop{0};
    std::atomic<uint64_t> captureCopyGpuOutstandingDepth{0};
    std::atomic<uint64_t> captureFrame{0};
    std::atomic<uint64_t> captureGpuWait{0};
    std::atomic<uint64_t> cpuAllSlotsBusyDrop{0};
    std::atomic<uint64_t> cpuLatestReplacement{0};
    std::atomic<uint64_t> dropped{0};
    std::atomic<uint64_t> gpuUpload{0};
    std::atomic<uint64_t> ingestAccepted{0};
    std::atomic<uint64_t> ingestCopyFailure{0};
    std::atomic<uint64_t> ingestCopyFrame{0};
    std::atomic<uint64_t> latestArrivalIntervalUs{0};
    std::atomic<uint64_t> latestCallbackCpuUs{0};
    std::atomic<uint64_t> latestCallbackToRequestNextUs{0};
    std::atomic<uint64_t> latestCaptureCopyGpuCompletionUs{0};
    std::atomic<uint64_t> latestCaptureCopyGpuSubmitUs{0};
    std::atomic<int64_t> latestCaptureHostTicks{0};
    std::atomic<uint64_t> latestCaptureSequence{0};
    std::atomic<uint64_t> latestCpuNv12CopyBytes{0};
    std::atomic<uint64_t> latestCpuNv12CopyUs{0};
    std::atomic<uint64_t> latestGpuUploadSubmitUs{0};
    std::atomic<uint64_t> latestIngestCopySubmitUs{0};
    std::atomic<int64_t> latestIngestHostTicks{0};
    std::atomic<uint64_t> latestIngestSequence{0};
    std::atomic<uint64_t> latestMfSampleResidenceUs{0};
    std::atomic<uint64_t> latestProcessedSourceAgeUs{0};
    std::atomic<uint64_t> latestProcessingCpuUs{0};
    std::atomic<uint64_t> latestRequestNextCpuUs{0};
    std::atomic<int64_t> latestSourceHostTicks{0};
    std::atomic<uint64_t> latestSourceIntervalUs{0};
    std::atomic<uint64_t> latestSourceSequence{0};
    std::atomic<uint64_t> latestSourceTimestampIntervalUs{0};
    std::atomic<uint64_t> presentedFrame{0};
    std::atomic<uint64_t> processingFailure{0};
    std::atomic<uint64_t> processingGpuWait{0};
    std::atomic<uint64_t> readyReplacement{0};
    std::atomic<uint64_t> sourceFrame{0};
    std::atomic<uint64_t> superseded{0};
};

static Telemetry g_t;

#define KIWI_RESET_ATOMIC(field) g_t.field.store(0, std::memory_order_relaxed)
static void ResetTelemetry() noexcept {
    KIWI_RESET_ATOMIC(allSlotsBusyDrop);
    KIWI_RESET_ATOMIC(captureCopyGpuOutstandingDepth);
    KIWI_RESET_ATOMIC(captureFrame);
    KIWI_RESET_ATOMIC(captureGpuWait);
    KIWI_RESET_ATOMIC(cpuAllSlotsBusyDrop);
    KIWI_RESET_ATOMIC(cpuLatestReplacement);
    KIWI_RESET_ATOMIC(dropped);
    KIWI_RESET_ATOMIC(gpuUpload);
    KIWI_RESET_ATOMIC(ingestAccepted);
    KIWI_RESET_ATOMIC(ingestCopyFailure);
    KIWI_RESET_ATOMIC(ingestCopyFrame);
    KIWI_RESET_ATOMIC(latestArrivalIntervalUs);
    KIWI_RESET_ATOMIC(latestCallbackCpuUs);
    KIWI_RESET_ATOMIC(latestCallbackToRequestNextUs);
    KIWI_RESET_ATOMIC(latestCaptureCopyGpuCompletionUs);
    KIWI_RESET_ATOMIC(latestCaptureCopyGpuSubmitUs);
    KIWI_RESET_ATOMIC(latestCaptureHostTicks);
    KIWI_RESET_ATOMIC(latestCaptureSequence);
    KIWI_RESET_ATOMIC(latestCpuNv12CopyBytes);
    KIWI_RESET_ATOMIC(latestCpuNv12CopyUs);
    KIWI_RESET_ATOMIC(latestGpuUploadSubmitUs);
    KIWI_RESET_ATOMIC(latestIngestCopySubmitUs);
    KIWI_RESET_ATOMIC(latestIngestHostTicks);
    KIWI_RESET_ATOMIC(latestIngestSequence);
    KIWI_RESET_ATOMIC(latestMfSampleResidenceUs);
    KIWI_RESET_ATOMIC(latestProcessedSourceAgeUs);
    KIWI_RESET_ATOMIC(latestProcessingCpuUs);
    KIWI_RESET_ATOMIC(latestRequestNextCpuUs);
    KIWI_RESET_ATOMIC(latestSourceHostTicks);
    KIWI_RESET_ATOMIC(latestSourceIntervalUs);
    KIWI_RESET_ATOMIC(latestSourceSequence);
    KIWI_RESET_ATOMIC(latestSourceTimestampIntervalUs);
    KIWI_RESET_ATOMIC(presentedFrame);
    KIWI_RESET_ATOMIC(processingFailure);
    KIWI_RESET_ATOMIC(processingGpuWait);
    KIWI_RESET_ATOMIC(readyReplacement);
    KIWI_RESET_ATOMIC(sourceFrame);
    KIWI_RESET_ATOMIC(superseded);
}
#undef KIWI_RESET_ATOMIC

static std::mutex g_errorMutex;
static int32_t g_lastErrorCode = 0;
static std::string g_lastErrorMessage;

static void SetError(HRESULT hr, std::string message) {
    std::lock_guard<std::mutex> lock(g_errorMutex);
    g_lastErrorCode = static_cast<int32_t>(hr);
    g_lastErrorMessage = std::move(message);
}

static void ClearError() {
    std::lock_guard<std::mutex> lock(g_errorMutex);
    g_lastErrorCode = 0;
    g_lastErrorMessage.clear();
}

static HRESULT Fail(HRESULT hr, const char* stage) {
    char text[256]{};
    sprintf_s(text, "%s failed: HRESULT=0x%08X", stage,
              static_cast<unsigned>(hr));
    SetError(hr, text);
    return hr;
}

struct alignas(16) ColorConstants final {
    float yOffset;
    float yScale;
    float cOffset;
    float cScale;
    float rCr;
    float gCb;
    float gCr;
    float bCb;
};
static_assert(sizeof(ColorConstants) == 32, "D3D11 cbuffer layout changed");

struct ColorMetadata final {
    UINT32 rawRange = MFNominalRange_Unknown;
    UINT32 rawMatrix = MFVideoTransferMatrix_Unknown;
    UINT32 rawPrimaries = 0;
    UINT32 rawTransfer = 0;
    bool hasRange = false;
    bool hasMatrix = false;
    bool hasPrimaries = false;
    bool hasTransfer = false;
    bool fullRange = false;
    bool bt601 = false;
    bool fallbackUsed = false;
    bool unsupported = false;
};

static ColorMetadata g_colorMetadata;
static ColorConstants g_colorConstants{};

static void EmitColorDiagnostic(const ColorMetadata& m) {
    char text[384]{};
    sprintf_s(text,
        "[KiwiNativeCamera candidate] color range=%u(%s) matrix=%u(%s) "
        "primaries=%u transfer=%u -> BT.%s %s%s\n",
        m.rawRange, m.hasRange ? "present" : "absent",
        m.rawMatrix, m.hasMatrix ? "present" : "absent",
        m.rawPrimaries, m.rawTransfer,
        m.bt601 ? "601" : "709",
        m.fullRange ? "Full" : "Limited",
        m.fallbackUsed ? " [fallback]" : "");
    OutputDebugStringA(text);
}

static ColorConstants MakeColorConstants(bool bt601, bool fullRange) {
    ColorConstants c{};
    c.yOffset = fullRange ? 0.0f : (16.0f / 255.0f);
    c.yScale = fullRange ? 1.0f : (255.0f / 219.0f);
    c.cOffset = 128.0f / 255.0f;
    c.cScale = fullRange ? 1.0f : (255.0f / 224.0f);
    if (bt601) {
        c.rCr = 1.402000f;
        c.gCb = 0.344136f;
        c.gCr = 0.714136f;
        c.bCb = 1.772000f;
    } else {
        c.rCr = 1.574800f;
        c.gCb = 0.187324f;
        c.gCr = 0.468124f;
        c.bCb = 1.855600f;
    }
    return c;
}

static ColorMetadata ResolveColorMetadata(IMFMediaType* type) {
    ColorMetadata m{};
    m.hasRange = type && SUCCEEDED(type->GetUINT32(
        MF_MT_VIDEO_NOMINAL_RANGE, &m.rawRange));
    m.hasMatrix = type && SUCCEEDED(type->GetUINT32(
        MF_MT_YUV_MATRIX, &m.rawMatrix));
    m.hasPrimaries = type && SUCCEEDED(type->GetUINT32(
        MF_MT_VIDEO_PRIMARIES, &m.rawPrimaries));
    m.hasTransfer = type && SUCCEEDED(type->GetUINT32(
        MF_MT_TRANSFER_FUNCTION, &m.rawTransfer));

    // Microsoft specifies Unknown matrix as BT.709.
    switch (m.rawMatrix) {
    case MFVideoTransferMatrix_BT601:
        m.bt601 = true;
        break;
    case MFVideoTransferMatrix_Unknown:
    case MFVideoTransferMatrix_BT709:
        m.bt601 = false;
        m.fallbackUsed |= !m.hasMatrix ||
                          m.rawMatrix == MFVideoTransferMatrix_Unknown;
        break;
    default:
        m.unsupported = true;
        break;
    }

    // There is no universal MF capture-source rule for Unknown range.
    // Kiwi's explicit compatibility policy is Limited when range is unknown.
    switch (m.rawRange) {
    case MFNominalRange_0_255:
        m.fullRange = true;
        break;
    case MFNominalRange_16_235:
        m.fullRange = false;
        break;
    case MFNominalRange_Unknown:
        m.fullRange = false;
        m.fallbackUsed = true;
        break;
    default:
        m.unsupported = true;
        break;
    }

    if (m.unsupported) {
        // Availability-first minimal candidate: visible warning, 709 Limited.
        m.bt601 = false;
        m.fullRange = false;
        m.fallbackUsed = true;
    }
    EmitColorDiagnostic(m);
    return m;
}

static constexpr char kNv12Shader[] = R"KIWI(
Texture2D<float> KiwiY : register(t0);
Texture2D<float2> KiwiUV : register(t1);
RWTexture2D<float4> KiwiOut : register(u0);
cbuffer KiwiColor : register(b0)
{
    float4 KiwiRange;  // yOffset, yScale, cOffset, cScale
    float4 KiwiMatrix; // rCr, gCb, gCr, bCb
};
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    KiwiOut.GetDimensions(width, height);
    if (id.x >= width || id.y >= height) return;
    float ySample = KiwiY.Load(int3(id.xy, 0));
    float2 uvSample = KiwiUV.Load(int3(id.xy / 2, 0));
    float y = (ySample - KiwiRange.x) * KiwiRange.y;
    float cb = (uvSample.x - KiwiRange.z) * KiwiRange.w;
    float cr = (uvSample.y - KiwiRange.z) * KiwiRange.w;
    float3 rgb;
    rgb.r = y + KiwiMatrix.x * cr;
    rgb.g = y - KiwiMatrix.y * cb - KiwiMatrix.z * cr;
    rgb.b = y + KiwiMatrix.w * cb;
    KiwiOut[id.xy] = float4(saturate(rgb), 1.0);
}
)KIWI";

enum class CpuState : uint8_t { Free, Writing, Ready, InUse };
enum class GpuState : uint8_t { Free, Processing, Ready, PresentRequested, AwaitUnity };

struct CpuSlot final {
    std::vector<uint8_t> bytes;
    CpuState state = CpuState::Free;
    uint64_t sequence = 0;
    int64_t hostTicks = 0;
};

struct GpuSlot final {
    ComPtr<ID3D11Texture2D> nv12;
    ComPtr<ID3D11ShaderResourceView1> ySrv;
    ComPtr<ID3D11ShaderResourceView1> uvSrv;
    ComPtr<ID3D11Texture2D> rgba;
    ComPtr<ID3D11UnorderedAccessView> rgbaUav;
    ComPtr<ID3D12Resource> shared12;
    ComPtr<ID3D11Texture2D> shared11;
    GpuState state = GpuState::Free;
    uint64_t sequence = 0;
    int64_t hostTicks = 0;
    uint64_t producerFenceValue = 0;
    uint64_t unityFenceValue = 0;
};

class CameraCallback;

static std::mutex g_startStopMutex;
static std::atomic<bool> g_running{false};
static std::atomic<bool> g_workerRunning{false};
static std::atomic<bool> g_unitySupported{false};
static std::atomic<bool> g_mfStarted{false};

static UINT32 g_width = 0;
static UINT32 g_height = 0;
static UINT32 g_fps = 0;
static std::atomic<uint64_t> g_nextSequence{0};
static std::atomic<uint64_t> g_lastRequestedSequence{0};
static std::atomic<uint64_t> g_producerFenceValue{0};
static std::atomic<int64_t> g_previousArrivalTicks{0};
static std::atomic<LONGLONG> g_previousSampleTimestamp{0};

static IUnityInterfaces* g_unityInterfaces = nullptr;       // borrowed
static IUnityGraphics* g_unityGraphics = nullptr;           // borrowed
static IUnityGraphicsD3D12v6* g_unityD3D12 = nullptr;       // borrowed
static ComPtr<ID3D12Device> g_device12;
static ComPtr<ID3D12Fence> g_unityFrameFence;

static ComPtr<IDXGIAdapter1> g_adapter;
static ComPtr<ID3D11Device> g_device11;
static ComPtr<ID3D11Device1> g_device11_1;
static ComPtr<ID3D11Device5> g_device11_5;
static ComPtr<ID3D11DeviceContext> g_context11;
static ComPtr<ID3D11DeviceContext4> g_context11_4;
static ComPtr<ID3D10Multithread> g_d3dMultithread;
static ComPtr<ID3D12CompatibilityDevice> g_compatibilityDevice;
static ComPtr<ID3D11ComputeShader> g_computeShader;
static ComPtr<ID3D11Buffer> g_colorBuffer;
static ComPtr<ID3D11Texture2D> g_ingestNv12;
static ComPtr<ID3D12Fence> g_producerFence12;
static ComPtr<ID3D11Fence> g_producerFence11;

static ComPtr<IMFMediaSource> g_mediaSource;
static ComPtr<IMFAttributes> g_readerAttributes;
static ComPtr<IMFSourceReader> g_reader;
static ComPtr<CameraCallback> g_callback;

static std::array<CpuSlot, kSlotCount> g_cpuSlots;
static std::mutex g_cpuMutex;
static std::condition_variable g_cpuCv;
static std::thread g_worker;

static std::array<GpuSlot, kSlotCount> g_gpuSlots;
static std::mutex g_gpuMutex;

static int AcquireCpuWriteSlot() {
    std::lock_guard<std::mutex> lock(g_cpuMutex);
    for (int i = 0; i < kSlotCount; ++i) {
        if (g_cpuSlots[i].state == CpuState::Free) {
            g_cpuSlots[i].state = CpuState::Writing;
            return i;
        }
    }
    int oldest = -1;
    uint64_t oldestSequence = std::numeric_limits<uint64_t>::max();
    for (int i = 0; i < kSlotCount; ++i) {
        if (g_cpuSlots[i].state == CpuState::Ready &&
            g_cpuSlots[i].sequence < oldestSequence) {
            oldest = i;
            oldestSequence = g_cpuSlots[i].sequence;
        }
    }
    if (oldest >= 0) {
        g_cpuSlots[oldest].state = CpuState::Writing;
        g_t.cpuLatestReplacement.fetch_add(1, std::memory_order_relaxed);
        g_t.superseded.fetch_add(1, std::memory_order_relaxed);
        return oldest;
    }
    g_t.cpuAllSlotsBusyDrop.fetch_add(1, std::memory_order_relaxed);
    g_t.dropped.fetch_add(1, std::memory_order_relaxed);
    return -1;
}

static void AbandonCpuWriteSlot(int index) {
    if (index < 0 || index >= kSlotCount) return;
    std::lock_guard<std::mutex> lock(g_cpuMutex);
    if (g_cpuSlots[index].state == CpuState::Writing)
        g_cpuSlots[index].state = CpuState::Free;
}

static void CommitCpuWriteSlot(int index, uint64_t sequence, int64_t hostTicks) {
    {
        std::lock_guard<std::mutex> lock(g_cpuMutex);
        CpuSlot& slot = g_cpuSlots[index];
        slot.sequence = sequence;
        slot.hostTicks = hostTicks;
        slot.state = CpuState::Ready;
    }
    g_cpuCv.notify_one();
}

static int AcquireLatestCpuReadSlot(uint64_t& sequence, int64_t& hostTicks) {
    std::unique_lock<std::mutex> lock(g_cpuMutex);
    g_cpuCv.wait(lock, [] {
        if (!g_running.load(std::memory_order_acquire)) return true;
        for (const CpuSlot& slot : g_cpuSlots)
            if (slot.state == CpuState::Ready) return true;
        return false;
    });

    int newest = -1;
    uint64_t newestSequence = 0;
    for (int i = 0; i < kSlotCount; ++i) {
        if (g_cpuSlots[i].state == CpuState::Ready &&
            (newest < 0 || g_cpuSlots[i].sequence > newestSequence)) {
            newest = i;
            newestSequence = g_cpuSlots[i].sequence;
        }
    }
    if (newest < 0) return -1;

    for (int i = 0; i < kSlotCount; ++i) {
        if (i != newest && g_cpuSlots[i].state == CpuState::Ready) {
            g_cpuSlots[i].state = CpuState::Free;
            g_t.superseded.fetch_add(1, std::memory_order_relaxed);
        }
    }
    CpuSlot& chosen = g_cpuSlots[newest];
    chosen.state = CpuState::InUse;
    sequence = chosen.sequence;
    hostTicks = chosen.hostTicks;
    return newest;
}

static void ReleaseCpuReadSlot(int index, uint64_t sequence) {
    if (index < 0 || index >= kSlotCount) return;
    std::lock_guard<std::mutex> lock(g_cpuMutex);
    CpuSlot& slot = g_cpuSlots[index];
    if (slot.state == CpuState::InUse && slot.sequence == sequence)
        slot.state = CpuState::Free;
}

static bool PointerRangeContains(
    const BYTE* allocationStart,
    DWORD allocationBytes,
    const BYTE* row,
    size_t rowBytes) noexcept {
    if (!allocationStart || allocationBytes == 0) return true;
    const uintptr_t begin = reinterpret_cast<uintptr_t>(allocationStart);
    const uintptr_t end = begin + allocationBytes;
    const uintptr_t p = reinterpret_cast<uintptr_t>(row);
    return p >= begin && p <= end && rowBytes <= end - p;
}

static bool CopyPitchedNv12(
    BYTE* destination,
    const BYTE* scanline0,
    LONG pitch,
    const BYTE* allocationStart,
    DWORD allocationBytes) {
    if (!destination || !scanline0 || pitch == 0 || g_width == 0 || g_height == 0)
        return false;
    const size_t absPitch = static_cast<size_t>(pitch > 0 ? pitch : -pitch);
    if (absPitch < g_width) return false;

    const BYTE* y0 = scanline0;
    const BYTE* uv0 = nullptr;
    if (pitch > 0) {
        uv0 = y0 + absPitch * g_height;
    } else {
        const BYTE* physicalYStart = y0 - absPitch * (g_height - 1);
        const BYTE* physicalUvStart = physicalYStart + absPitch * g_height;
        uv0 = physicalUvStart + absPitch * (g_height / 2 - 1);
    }

    for (UINT32 y = 0; y < g_height; ++y) {
        const BYTE* src = y0 + static_cast<ptrdiff_t>(pitch) * y;
        if (!PointerRangeContains(allocationStart, allocationBytes, src, g_width))
            return false;
        std::memcpy(destination + static_cast<size_t>(y) * g_width, src, g_width);
    }
    BYTE* uvDestination = destination + static_cast<size_t>(g_width) * g_height;
    for (UINT32 y = 0; y < g_height / 2; ++y) {
        const BYTE* src = uv0 + static_cast<ptrdiff_t>(pitch) * y;
        if (!PointerRangeContains(allocationStart, allocationBytes, src, g_width))
            return false;
        std::memcpy(uvDestination + static_cast<size_t>(y) * g_width, src, g_width);
    }
    return true;
}

static bool CopyMediaBufferToCpu(IMFMediaBuffer* buffer, BYTE* destination) {
    if (!buffer || !destination) return false;

    ComPtr<IMF2DBuffer2> twoD2;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&twoD2)))) {
        BYTE* scanline0 = nullptr;
        BYTE* allocationStart = nullptr;
        LONG pitch = 0;
        DWORD allocationBytes = 0;
        HRESULT hr = twoD2->Lock2DSize(
            MF2DBuffer_LockFlags_Read,
            &scanline0, &pitch, &allocationStart, &allocationBytes);
        if (SUCCEEDED(hr)) {
            const bool copied = CopyPitchedNv12(
                destination, scanline0, pitch, allocationStart, allocationBytes);
            twoD2->Unlock2D();
            if (copied) return true;
        }
    }

    ComPtr<IMF2DBuffer> twoD;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&twoD)))) {
        BYTE* scanline0 = nullptr;
        LONG pitch = 0;
        HRESULT hr = twoD->Lock2D(&scanline0, &pitch);
        if (SUCCEEDED(hr)) {
            const bool copied = CopyPitchedNv12(
                destination, scanline0, pitch, nullptr, 0);
            twoD->Unlock2D();
            if (copied) return true;
        }
    }

    BYTE* bytes = nullptr;
    DWORD maxBytes = 0;
    DWORD currentBytes = 0;
    HRESULT hr = buffer->Lock(&bytes, &maxBytes, &currentBytes);
    if (FAILED(hr)) return false;
    const size_t expected = static_cast<size_t>(g_width) * g_height * 3 / 2;
    const bool copied = bytes && currentBytes >= expected;
    if (copied) std::memcpy(destination, bytes, expected);
    buffer->Unlock();
    return copied;
}

static bool CopySampleToCpuLatest(
    IMFSample* sample, uint64_t sequence, int64_t hostTicks) {
    const int64_t begin = QpcNow();
    if (!sample) return false;

    ComPtr<IMFMediaBuffer> buffer;
    DWORD count = 0;
    HRESULT hr = sample->GetBufferCount(&count);
    if (FAILED(hr) || count == 0) return false;
    if (count == 1)
        hr = sample->GetBufferByIndex(0, &buffer);
    else
        hr = sample->ConvertToContiguousBuffer(&buffer);
    if (FAILED(hr) || !buffer) return false;

    const int slotIndex = AcquireCpuWriteSlot();
    if (slotIndex < 0) return false;
    CpuSlot& slot = g_cpuSlots[slotIndex];
    const size_t expected = static_cast<size_t>(g_width) * g_height * 3 / 2;
    if (slot.bytes.size() != expected) slot.bytes.resize(expected);

    if (!CopyMediaBufferToCpu(buffer.Get(), slot.bytes.data())) {
        AbandonCpuWriteSlot(slotIndex);
        return false;
    }

    CommitCpuWriteSlot(slotIndex, sequence, hostTicks);
    const uint64_t elapsed = QpcToMicroseconds(QpcNow() - begin);
    g_t.captureFrame.fetch_add(1, std::memory_order_relaxed);
    g_t.latestCaptureSequence.store(sequence, std::memory_order_relaxed);
    g_t.latestCaptureHostTicks.store(hostTicks, std::memory_order_relaxed);
    g_t.latestCpuNv12CopyBytes.store(expected, std::memory_order_relaxed);
    g_t.latestCpuNv12CopyUs.store(elapsed, std::memory_order_relaxed);
    return true;
}

class CameraCallback final : public IMFSourceReaderCallback {
public:
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IMFSourceReaderCallback)) {
            *out = static_cast<IMFSourceReaderCallback*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override {
        return refs_.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    STDMETHODIMP_(ULONG) Release() override {
        const ULONG refs = refs_.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (refs == 0) delete this;
        return refs;
    }

    void AttachReader(IMFSourceReader* reader) {
        std::lock_guard<std::mutex> lock(readerMutex_);
        reader_ = reader;
    }

    void DetachReader() {
        std::lock_guard<std::mutex> lock(readerMutex_);
        reader_.Reset();
    }

    HRESULT RequestNext() {
        const int64_t begin = QpcNow();
        ComPtr<IMFSourceReader> reader;
        {
            std::lock_guard<std::mutex> lock(readerMutex_);
            reader = reader_;
        }
        if (!reader || !g_running.load(std::memory_order_acquire)) return S_FALSE;
        HRESULT hr = reader->ReadSample(
            MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
            nullptr, nullptr, nullptr, nullptr);
        g_t.latestRequestNextCpuUs.store(
            QpcToMicroseconds(QpcNow() - begin), std::memory_order_relaxed);
        if (FAILED(hr)) {
            Fail(hr, "IMFSourceReader::ReadSample");
            g_running.store(false, std::memory_order_release);
        }
        return hr;
    }

    STDMETHODIMP OnReadSample(
        HRESULT status,
        DWORD /*streamIndex*/,
        DWORD streamFlags,
        LONGLONG timestamp,
        IMFSample* sample) override {
        const int64_t callbackBegin = QpcNow();
        if (!g_running.load(std::memory_order_acquire)) return S_OK;
        if (FAILED(status)) {
            Fail(status, "SourceReader callback status");
            g_running.store(false, std::memory_order_release);
            g_cpuCv.notify_all();
            return S_OK;
        }
        if ((streamFlags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) {
            SetError(MF_E_END_OF_STREAM, "SourceReader reached end of stream");
            g_running.store(false, std::memory_order_release);
            g_cpuCv.notify_all();
            return S_OK;
        }

        const int64_t arrival = QpcNow();
        const int64_t previousArrival = g_previousArrivalTicks.exchange(arrival);
        if (previousArrival != 0) {
            const uint64_t interval = QpcToMicroseconds(arrival - previousArrival);
            g_t.latestArrivalIntervalUs.store(interval, std::memory_order_relaxed);
            g_t.latestSourceIntervalUs.store(interval, std::memory_order_relaxed);
        }
        const LONGLONG previousTimestamp = g_previousSampleTimestamp.exchange(timestamp);
        if (previousTimestamp != 0 && timestamp >= previousTimestamp)
            g_t.latestSourceTimestampIntervalUs.store(
                static_cast<uint64_t>((timestamp - previousTimestamp) / 10),
                std::memory_order_relaxed);

        const uint64_t sequence = g_nextSequence.fetch_add(1) + 1;
        g_t.sourceFrame.fetch_add(1, std::memory_order_relaxed);
        g_t.latestSourceSequence.store(sequence, std::memory_order_relaxed);
        g_t.latestSourceHostTicks.store(arrival, std::memory_order_relaxed);

        if (!CopySampleToCpuLatest(sample, sequence, arrival))
            g_t.dropped.fetch_add(1, std::memory_order_relaxed);

        const int64_t beforeRequest = QpcNow();
        RequestNext();
        const int64_t callbackEnd = QpcNow();
        g_t.latestCallbackToRequestNextUs.store(
            QpcToMicroseconds(beforeRequest - callbackBegin),
            std::memory_order_relaxed);
        g_t.latestCallbackCpuUs.store(
            QpcToMicroseconds(callbackEnd - callbackBegin),
            std::memory_order_relaxed);
        g_t.latestMfSampleResidenceUs.store(
            QpcToMicroseconds(callbackEnd - callbackBegin),
            std::memory_order_relaxed);
        return S_OK;
    }

    STDMETHODIMP OnFlush(DWORD) override { return S_OK; }
    STDMETHODIMP OnEvent(DWORD, IMFMediaEvent*) override { return S_OK; }

private:
    ~CameraCallback() = default;
    std::atomic<ULONG> refs_{1};
    std::mutex readerMutex_;
    ComPtr<IMFSourceReader> reader_;
};

static HRESULT CompileNv12Shader() {
    ComPtr<ID3DBlob> code;
    ComPtr<ID3DBlob> errors;
    HRESULT hr = D3DCompile(
        kNv12Shader, sizeof(kNv12Shader) - 1,
        "KiwiNv12ToRgba", nullptr, nullptr,
        "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
        &code, &errors);
    if (FAILED(hr)) {
        std::string message("D3DCompile failed");
        if (errors && errors->GetBufferPointer() && errors->GetBufferSize()) {
            message.append(": ");
            message.append(
                static_cast<const char*>(errors->GetBufferPointer()),
                errors->GetBufferSize());
        }
        SetError(hr, std::move(message));
        return hr;
    }
    hr = g_device11->CreateComputeShader(
        code->GetBufferPointer(), code->GetBufferSize(),
        nullptr, &g_computeShader);
    return FAILED(hr) ? Fail(hr, "ID3D11Device::CreateComputeShader") : S_OK;
}

static HRESULT CreateColorConstantBuffer() {
    g_colorConstants = MakeColorConstants(
        g_colorMetadata.bt601, g_colorMetadata.fullRange);
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = sizeof(ColorConstants);
    desc.Usage = D3D11_USAGE_IMMUTABLE;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SUBRESOURCE_DATA initial{};
    initial.pSysMem = &g_colorConstants;
    HRESULT hr = g_device11->CreateBuffer(&desc, &initial, &g_colorBuffer);
    return FAILED(hr) ? Fail(hr, "Create color constant buffer") : S_OK;
}

static HRESULT CreateSameAdapterD3D11Device() {
    if (!g_device12) return Fail(E_POINTER, "Unity D3D12 device");

    ComPtr<IDXGIFactory4> factory;
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return Fail(hr, "CreateDXGIFactory2");
    hr = factory->EnumAdapterByLuid(
        g_device12->GetAdapterLuid(), IID_PPV_ARGS(&g_adapter));
    if (FAILED(hr)) return Fail(hr, "EnumAdapterByLuid");

    const D3D_FEATURE_LEVEL levels[] = {
        D3D_FEATURE_LEVEL_12_1,
        D3D_FEATURE_LEVEL_12_0,
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
    };
    D3D_FEATURE_LEVEL createdLevel{};
    const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT |
                       D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    hr = D3D11CreateDevice(
        g_adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
        levels, static_cast<UINT>(std::size(levels)),
        D3D11_SDK_VERSION, &g_device11, &createdLevel, &g_context11);
    if (FAILED(hr)) return Fail(hr, "D3D11CreateDevice(same adapter)");

    hr = g_device11.As(&g_device11_1);
    if (FAILED(hr)) return Fail(hr, "Query ID3D11Device1");
    hr = g_device11.As(&g_device11_5);
    if (FAILED(hr)) return Fail(hr, "Query ID3D11Device5");
    hr = g_context11.As(&g_context11_4);
    if (FAILED(hr)) return Fail(hr, "Query ID3D11DeviceContext4");
    if (SUCCEEDED(g_context11.As(&g_d3dMultithread)) && g_d3dMultithread)
        g_d3dMultithread->SetMultithreadProtected(TRUE);
    hr = g_device12.As(&g_compatibilityDevice);
    return FAILED(hr) ? Fail(hr, "Query ID3D12CompatibilityDevice") : S_OK;
}

static HRESULT CreateProducerFence() {
    HRESULT hr = g_device12->CreateFence(
        0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g_producerFence12));
    if (FAILED(hr)) return Fail(hr, "ID3D12Device::CreateFence");
    HANDLE handle = nullptr;
    hr = g_device12->CreateSharedHandle(
        g_producerFence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
    if (FAILED(hr)) return Fail(hr, "CreateSharedHandle(producer fence)");
    hr = g_device11_5->OpenSharedFence(
        handle, IID_PPV_ARGS(&g_producerFence11));
    CloseHandle(handle);
    return FAILED(hr) ? Fail(hr, "ID3D11Device5::OpenSharedFence") : S_OK;
}

static HRESULT CreateReverseSharedResource(GpuSlot& slot) {
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;

    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = g_width;
    desc.Height = g_height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

    D3D11_RESOURCE_FLAGS flags11{};
    flags11.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    flags11.MiscFlags = D3D11_RESOURCE_MISC_SHARED |
                        D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    HRESULT hr = g_compatibilityDevice->CreateSharedResource(
        &heap, D3D12_HEAP_FLAG_SHARED, &desc,
        D3D12_RESOURCE_STATE_COMMON, nullptr, &flags11,
        D3D12_COMPATIBILITY_SHARED_FLAG_NONE,
        nullptr, nullptr, IID_PPV_ARGS(&slot.shared12));
    if (FAILED(hr)) return Fail(hr, "ID3D12CompatibilityDevice::CreateSharedResource");

    // Production calls both property types. Keep the checks diagnostic-only.
    D3D11_RESOURCE_FLAGS reflectedFlags{};
    D3D12_COMPATIBILITY_SHARED_FLAGS reflectedCompatibility{};
    g_compatibilityDevice->ReflectSharedProperties(
        slot.shared12.Get(), D3D12_REFLECT_SHARED_PROPERTY_D3D11_RESOURCE_FLAGS,
        &reflectedFlags, sizeof(reflectedFlags));
    g_compatibilityDevice->ReflectSharedProperties(
        slot.shared12.Get(),
        D3D12_REFELCT_SHARED_PROPERTY_COMPATIBILITY_SHARED_FLAGS,
        &reflectedCompatibility, sizeof(reflectedCompatibility));

    HANDLE handle = nullptr;
    hr = g_device12->CreateSharedHandle(
        slot.shared12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
    if (FAILED(hr)) return Fail(hr, "CreateSharedHandle(RGBA resource)");
    hr = g_device11_1->OpenSharedResource1(
        handle, IID_PPV_ARGS(&slot.shared11));
    CloseHandle(handle);
    if (FAILED(hr)) return Fail(hr, "ID3D11Device1::OpenSharedResource1");

    D3D11_TEXTURE2D_DESC opened{};
    slot.shared11->GetDesc(&opened);
    if (opened.Width != g_width || opened.Height != g_height ||
        opened.Format != DXGI_FORMAT_R8G8B8A8_UNORM)
        return Fail(E_UNEXPECTED, "Verify reverse-shared RGBA texture");
    return S_OK;
}

static HRESULT CreateGpuAndCpuSlots() {
    const size_t nv12Bytes = static_cast<size_t>(g_width) * g_height * 3 / 2;
    for (CpuSlot& slot : g_cpuSlots) {
        slot.bytes.assign(nv12Bytes, 0);
        slot.state = CpuState::Free;
        slot.sequence = 0;
        slot.hostTicks = 0;
    }

    D3D11_TEXTURE2D_DESC ingest{};
    ingest.Width = g_width;
    ingest.Height = g_height;
    ingest.MipLevels = 1;
    ingest.ArraySize = 1;
    ingest.Format = DXGI_FORMAT_NV12;
    ingest.SampleDesc.Count = 1;
    ingest.Usage = D3D11_USAGE_DEFAULT;
    HRESULT hr = g_device11->CreateTexture2D(&ingest, nullptr, &g_ingestNv12);
    if (FAILED(hr)) return Fail(hr, "Create Path B ingest NV12 texture");

    for (GpuSlot& slot : g_gpuSlots) {
        slot = GpuSlot{};
        D3D11_TEXTURE2D_DESC nv12 = ingest;
        nv12.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        hr = g_device11->CreateTexture2D(&nv12, nullptr, &slot.nv12);
        if (FAILED(hr)) return Fail(hr, "Create GPU-slot NV12 texture");

        D3D11_SHADER_RESOURCE_VIEW_DESC1 y{};
        y.Format = DXGI_FORMAT_R8_UNORM;
        y.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        y.Texture2D.MostDetailedMip = 0;
        y.Texture2D.MipLevels = 1;
        y.Texture2D.PlaneSlice = 0;
        hr = g_device11_5->CreateShaderResourceView1(
            slot.nv12.Get(), &y, &slot.ySrv);
        if (FAILED(hr)) return Fail(hr, "Create NV12 Y-plane SRV");

        D3D11_SHADER_RESOURCE_VIEW_DESC1 uv = y;
        uv.Format = DXGI_FORMAT_R8G8_UNORM;
        uv.Texture2D.PlaneSlice = 1;
        hr = g_device11_5->CreateShaderResourceView1(
            slot.nv12.Get(), &uv, &slot.uvSrv);
        if (FAILED(hr)) return Fail(hr, "Create NV12 UV-plane SRV");

        D3D11_TEXTURE2D_DESC rgba{};
        rgba.Width = g_width;
        rgba.Height = g_height;
        rgba.MipLevels = 1;
        rgba.ArraySize = 1;
        rgba.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        rgba.SampleDesc.Count = 1;
        rgba.Usage = D3D11_USAGE_DEFAULT;
        rgba.BindFlags = D3D11_BIND_SHADER_RESOURCE |
                         D3D11_BIND_UNORDERED_ACCESS;
        hr = g_device11->CreateTexture2D(&rgba, nullptr, &slot.rgba);
        if (FAILED(hr)) return Fail(hr, "Create compute RGBA texture");
        hr = g_device11->CreateUnorderedAccessView(
            slot.rgba.Get(), nullptr, &slot.rgbaUav);
        if (FAILED(hr)) return Fail(hr, "Create compute RGBA UAV");
        hr = CreateReverseSharedResource(slot);
        if (FAILED(hr)) return hr;
    }
    return S_OK;
}

static void ReclaimUnityCompletedSlotsLocked() {
    const uint64_t completed = g_unityFrameFence
        ? g_unityFrameFence->GetCompletedValue() : 0;
    for (GpuSlot& slot : g_gpuSlots) {
        if (slot.state == GpuState::AwaitUnity &&
            slot.unityFenceValue <= completed) {
            slot.state = GpuState::Free;
        }
    }
}

static int AcquireGpuProcessingSlot() {
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    ReclaimUnityCompletedSlotsLocked();
    for (int i = 0; i < kSlotCount; ++i) {
        if (g_gpuSlots[i].state == GpuState::Free) {
            g_gpuSlots[i].state = GpuState::Processing;
            return i;
        }
    }
    int oldest = -1;
    uint64_t oldestSequence = std::numeric_limits<uint64_t>::max();
    for (int i = 0; i < kSlotCount; ++i) {
        if (g_gpuSlots[i].state == GpuState::Ready &&
            g_gpuSlots[i].sequence < oldestSequence) {
            oldest = i;
            oldestSequence = g_gpuSlots[i].sequence;
        }
    }
    if (oldest >= 0) {
        g_gpuSlots[oldest].state = GpuState::Processing;
        g_t.readyReplacement.fetch_add(1, std::memory_order_relaxed);
        g_t.superseded.fetch_add(1, std::memory_order_relaxed);
        return oldest;
    }
    g_t.allSlotsBusyDrop.fetch_add(1, std::memory_order_relaxed);
    g_t.dropped.fetch_add(1, std::memory_order_relaxed);
    return -1;
}

static void AbandonGpuProcessingSlot(int index) {
    if (index < 0 || index >= kSlotCount) return;
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    if (g_gpuSlots[index].state == GpuState::Processing)
        g_gpuSlots[index].state = GpuState::Free;
}

static HRESULT ProcessNv12OnGpu(
    int gpuIndex, uint64_t sequence, int64_t hostTicks) {
    const int64_t begin = QpcNow();
    GpuSlot& slot = g_gpuSlots[gpuIndex];
    g_context11->CopyResource(slot.nv12.Get(), g_ingestNv12.Get());

    ID3D11ShaderResourceView* srvs[] = { slot.ySrv.Get(), slot.uvSrv.Get() };
    ID3D11UnorderedAccessView* uavs[] = { slot.rgbaUav.Get() };
    ID3D11Buffer* buffers[] = { g_colorBuffer.Get() };
    g_context11->CSSetShader(g_computeShader.Get(), nullptr, 0);
    g_context11->CSSetShaderResources(0, 2, srvs);
    g_context11->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
    g_context11->CSSetConstantBuffers(0, 1, buffers);
    g_context11->Dispatch((g_width + 7) / 8, (g_height + 7) / 8, 1);

    ID3D11ShaderResourceView* nullSrvs[] = { nullptr, nullptr };
    ID3D11UnorderedAccessView* nullUavs[] = { nullptr };
    ID3D11Buffer* nullBuffers[] = { nullptr };
    g_context11->CSSetShaderResources(0, 2, nullSrvs);
    g_context11->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
    g_context11->CSSetConstantBuffers(0, 1, nullBuffers);
    g_context11->CSSetShader(nullptr, nullptr, 0);

    g_context11->CopyResource(slot.shared11.Get(), slot.rgba.Get());
    const uint64_t fenceValue = g_producerFenceValue.fetch_add(1) + 1;
    HRESULT hr = g_context11_4->Signal(g_producerFence11.Get(), fenceValue);
    g_context11->ClearState();
    if (FAILED(hr)) {
        AbandonGpuProcessingSlot(gpuIndex);
        g_t.processingFailure.fetch_add(1, std::memory_order_relaxed);
        return Fail(hr, "ID3D11DeviceContext4::Signal");
    }

    {
        std::lock_guard<std::mutex> lock(g_gpuMutex);
        slot.sequence = sequence;
        slot.hostTicks = hostTicks;
        slot.producerFenceValue = fenceValue;
        slot.unityFenceValue = 0;
        slot.state = GpuState::Ready;
    }
    const int64_t end = QpcNow();
    g_t.latestProcessingCpuUs.store(
        QpcToMicroseconds(end - begin), std::memory_order_relaxed);
    g_t.latestProcessedSourceAgeUs.store(
        QpcToMicroseconds(end - hostTicks), std::memory_order_relaxed);
    return S_OK;
}

static void ProcessingWorker() {
    g_workerRunning.store(true, std::memory_order_release);
    while (g_running.load(std::memory_order_acquire)) {
        uint64_t sequence = 0;
        int64_t hostTicks = 0;
        const int cpuIndex = AcquireLatestCpuReadSlot(sequence, hostTicks);
        if (cpuIndex < 0) continue;

        const int gpuIndex = AcquireGpuProcessingSlot();
        if (gpuIndex < 0) {
            ReleaseCpuReadSlot(cpuIndex, sequence);
            continue;
        }
        g_t.ingestAccepted.fetch_add(1, std::memory_order_relaxed);

        const int64_t uploadBegin = QpcNow();
        const CpuSlot& cpu = g_cpuSlots[cpuIndex];
        const UINT rowPitch = g_width;
        const UINT depthPitch = g_width * g_height * 3 / 2;
        g_context11->UpdateSubresource(
            g_ingestNv12.Get(), 0, nullptr,
            cpu.bytes.data(), rowPitch, depthPitch);
        const int64_t uploadEnd = QpcNow();
        ReleaseCpuReadSlot(cpuIndex, sequence); // legal after UpdateSubresource returns

        const uint64_t uploadUs = QpcToMicroseconds(uploadEnd - uploadBegin);
        g_t.gpuUpload.fetch_add(1, std::memory_order_relaxed);
        g_t.ingestCopyFrame.fetch_add(1, std::memory_order_relaxed);
        g_t.latestGpuUploadSubmitUs.store(uploadUs, std::memory_order_relaxed);
        g_t.latestIngestCopySubmitUs.store(uploadUs, std::memory_order_relaxed);
        g_t.latestIngestSequence.store(sequence, std::memory_order_relaxed);
        g_t.latestIngestHostTicks.store(hostTicks, std::memory_order_relaxed);

        if (FAILED(ProcessNv12OnGpu(gpuIndex, sequence, hostTicks)))
            g_t.ingestCopyFailure.fetch_add(1, std::memory_order_relaxed);
    }
    g_workerRunning.store(false, std::memory_order_release);
}

static std::wstring UpperInvariant(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(),
        [](wchar_t c) { return static_cast<wchar_t>(std::towupper(c)); });
    return value;
}

static HRESULT EnumerateAndSelectCamera(
    const wchar_t* requestedName, IMFMediaSource** mediaSource) {
    if (!mediaSource) return E_POINTER;
    *mediaSource = nullptr;
    ComPtr<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(&attributes, 1);
    if (FAILED(hr)) return Fail(hr, "MFCreateAttributes(device enumeration)");
    hr = attributes->SetGUID(
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (FAILED(hr)) return Fail(hr, "Set video-capture source type");

    IMFActivate** raw = nullptr;
    UINT32 count = 0;
    hr = MFEnumDeviceSources(attributes.Get(), &raw, &count);
    if (FAILED(hr)) return Fail(hr, "MFEnumDeviceSources");
    std::vector<ComPtr<IMFActivate>> devices;
    std::vector<std::wstring> names;
    devices.reserve(count);
    names.reserve(count);
    for (UINT32 i = 0; i < count; ++i) {
        ComPtr<IMFActivate> activate;
        activate.Attach(raw[i]);
        WCHAR* allocatedName = nullptr;
        UINT32 chars = 0;
        std::wstring name;
        if (SUCCEEDED(activate->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                &allocatedName, &chars)) && allocatedName) {
            name.assign(allocatedName, chars);
            CoTaskMemFree(allocatedName);
        }
        devices.push_back(std::move(activate));
        names.push_back(std::move(name));
    }
    CoTaskMemFree(raw);
    if (devices.empty()) return Fail(MF_E_NOT_FOUND, "No camera device");

    int selected = -1;
    if (requestedName && requestedName[0]) {
        for (size_t i = 0; i < names.size(); ++i) {
            if (_wcsicmp(names[i].c_str(), requestedName) == 0) {
                selected = static_cast<int>(i);
                break;
            }
        }
    }
    if (selected < 0) {
        for (size_t i = 0; i < names.size(); ++i) {
            const std::wstring upper = UpperInvariant(names[i]);
            if (upper.find(L"UGREEN") != std::wstring::npos ||
                upper.find(L"CM831") != std::wstring::npos) {
                selected = static_cast<int>(i);
                break;
            }
        }
    }
    if (selected < 0) selected = 0;
    // Friendly-name selection is deliberately unrelated to color metadata.
    hr = devices[selected]->ActivateObject(IID_PPV_ARGS(mediaSource));
    return FAILED(hr) ? Fail(hr, "IMFActivate::ActivateObject") : S_OK;
}

static bool MediaTypeMatches(
    IMFMediaType* type, UINT32 width, UINT32 height, UINT32 fps) {
    GUID subtype{};
    if (!type || FAILED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) ||
        subtype != MFVideoFormat_NV12)
        return false;
    UINT32 w = 0, h = 0, numerator = 0, denominator = 0;
    if (FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h)) ||
        w != width || h != height ||
        FAILED(MFGetAttributeRatio(
            type, MF_MT_FRAME_RATE, &numerator, &denominator)) ||
        denominator == 0)
        return false;
    const double actual = static_cast<double>(numerator) / denominator;
    return std::abs(actual - static_cast<double>(fps)) <= 0.5;
}

static HRESULT CreateMediaFoundationReader(const wchar_t* cameraName) {
    HRESULT hr = EnumerateAndSelectCamera(cameraName, &g_mediaSource);
    if (FAILED(hr)) return hr;

    CameraCallback* callback = new (std::nothrow) CameraCallback();
    if (!callback) return Fail(E_OUTOFMEMORY, "Create CameraCallback");
    g_callback.Attach(callback);

    hr = MFCreateAttributes(&g_readerAttributes, 6);
    if (FAILED(hr)) return Fail(hr, "MFCreateAttributes(Source Reader)");
    // Production Path B intentionally does NOT set MF_SOURCE_READER_D3D_MANAGER.
    hr = g_readerAttributes->SetUnknown(
        MF_SOURCE_READER_ASYNC_CALLBACK, g_callback.Get());
    if (FAILED(hr)) return Fail(hr, "Set Source Reader async callback");
    hr = g_readerAttributes->SetUINT32(MF_LOW_LATENCY, TRUE);
    if (FAILED(hr)) return Fail(hr, "Set MF_LOW_LATENCY");
    hr = g_readerAttributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE);
    if (FAILED(hr)) return Fail(hr, "Disable Source Reader converters");

    hr = MFCreateSourceReaderFromMediaSource(
        g_mediaSource.Get(), g_readerAttributes.Get(), &g_reader);
    if (FAILED(hr)) return Fail(hr, "MFCreateSourceReaderFromMediaSource");
    g_callback->AttachReader(g_reader.Get());

    ComPtr<IMFMediaType> selected;
    for (DWORD index = 0; ; ++index) {
        ComPtr<IMFMediaType> type;
        hr = g_reader->GetNativeMediaType(
            MF_SOURCE_READER_FIRST_VIDEO_STREAM, index, &type);
        if (FAILED(hr)) break;
        if (MediaTypeMatches(type.Get(), g_width, g_height, g_fps)) {
            selected = type;
            break;
        }
    }
    if (!selected)
        return Fail(MF_E_INVALIDMEDIATYPE, "No exact native NV12 media type");

    ComPtr<IMFSourceReaderEx> readerEx;
    hr = g_reader.As(&readerEx);
    if (FAILED(hr)) return Fail(hr, "Query IMFSourceReaderEx");
    DWORD nativeFlags = 0;
    hr = readerEx->SetNativeMediaType(
        MF_SOURCE_READER_FIRST_VIDEO_STREAM, selected.Get(), &nativeFlags);
    if (FAILED(hr)) return Fail(hr, "IMFSourceReaderEx::SetNativeMediaType");
    hr = g_reader->SetCurrentMediaType(
        MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, selected.Get());
    if (FAILED(hr)) return Fail(hr, "IMFSourceReader::SetCurrentMediaType");

    // Minimal metadata-driven addition: read the actual current type once/session.
    ComPtr<IMFMediaType> current;
    hr = g_reader->GetCurrentMediaType(
        MF_SOURCE_READER_FIRST_VIDEO_STREAM, &current);
    IMFMediaType* colorType = SUCCEEDED(hr) && current
        ? current.Get() : selected.Get();
    g_colorMetadata = ResolveColorMetadata(colorType);
    return CreateColorConstantBuffer();
}

static void ReleasePathResources() {
    g_colorBuffer.Reset();
    g_computeShader.Reset();
    g_ingestNv12.Reset();
    for (GpuSlot& slot : g_gpuSlots) slot = GpuSlot{};
    g_producerFence11.Reset();
    g_producerFence12.Reset();
    g_compatibilityDevice.Reset();
    g_d3dMultithread.Reset();
    g_context11_4.Reset();
    g_context11.Reset();
    g_device11_5.Reset();
    g_device11_1.Reset();
    g_device11.Reset();
    g_adapter.Reset();
}

static void StopUnlocked() {
    g_running.store(false, std::memory_order_release);
    g_cpuCv.notify_all();
    if (g_worker.joinable()) {
        // The candidate never calls Stop from ProcessingWorker itself.
        g_worker.join();
    }

    if (g_reader)
        g_reader->Flush(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    if (g_callback)
        g_callback->DetachReader();
    g_reader.Reset();
    g_readerAttributes.Reset();
    g_callback.Reset();
    if (g_mediaSource)
        g_mediaSource->Shutdown();
    g_mediaSource.Reset();

    ReleasePathResources();
    if (g_mfStarted.exchange(false, std::memory_order_acq_rel))
        MFShutdown();

    {
        std::lock_guard<std::mutex> lock(g_cpuMutex);
        for (CpuSlot& slot : g_cpuSlots) {
            slot.state = CpuState::Free;
            slot.sequence = 0;
            slot.hostTicks = 0;
            slot.bytes.clear();
        }
    }
    g_nextSequence.store(0);
    g_lastRequestedSequence.store(0);
    g_producerFenceValue.store(0);
    g_previousArrivalTicks.store(0);
    g_previousSampleTimestamp.store(0);
}

static int StartInternal(
    const wchar_t* cameraName, int width, int height, int fps, int mode) {
    std::lock_guard<std::mutex> lifecycle(g_startStopMutex);
    if (g_running.load(std::memory_order_acquire)) {
        SetError(MF_E_INVALIDREQUEST, "Camera is already running");
        return 0;
    }
    StopUnlocked();
    ClearError();
    ResetTelemetry();

    if (mode != kProductionMode) {
        SetError(E_NOTIMPL,
            "Reconstructed candidate implements Production diagnostic mode 9 only");
        return 0;
    }
    if (!g_unitySupported.load(std::memory_order_acquire) ||
        !g_unityD3D12 || !g_device12 || !g_unityFrameFence) {
        SetError(E_NOINTERFACE, "Unity D3D12 v6 is not initialized");
        return 0;
    }
    if (width <= 0 || height <= 0 || fps <= 0 ||
        (width & 1) != 0 || (height & 1) != 0) {
        SetError(E_INVALIDARG, "NV12 width/height must be positive and even");
        return 0;
    }
    g_width = static_cast<UINT32>(width);
    g_height = static_cast<UINT32>(height);
    g_fps = static_cast<UINT32>(fps);

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) {
        Fail(hr, "MFStartup");
        return 0;
    }
    g_mfStarted.store(true, std::memory_order_release);

    hr = CreateSameAdapterD3D11Device();
    if (SUCCEEDED(hr)) hr = CompileNv12Shader();
    if (SUCCEEDED(hr)) hr = CreateProducerFence();
    if (SUCCEEDED(hr)) hr = CreateGpuAndCpuSlots();
    if (SUCCEEDED(hr)) hr = CreateMediaFoundationReader(cameraName);
    if (FAILED(hr)) {
        StopUnlocked();
        return 0;
    }

    g_running.store(true, std::memory_order_release);
    try {
        g_worker = std::thread(ProcessingWorker);
    } catch (...) {
        SetError(E_FAIL, "Failed to create processing worker");
        StopUnlocked();
        return 0;
    }
    hr = g_callback->RequestNext();
    if (FAILED(hr)) {
        StopUnlocked();
        return 0;
    }
    return 1;
}

static uint64_t ProducerCompletedValue() {
    return g_producerFence12 ? g_producerFence12->GetCompletedValue() : 0;
}

static uint64_t ConsumerCompletedValue() {
    return g_unityFrameFence ? g_unityFrameFence->GetCompletedValue() : 0;
}

static uint64_t ProducerFenceLag() {
    const uint64_t produced = g_producerFenceValue.load();
    const uint64_t completed = ProducerCompletedValue();
    return produced > completed ? produced - completed : 0;
}

static uint64_t ConsumerFenceLag() {
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    const uint64_t completed = ConsumerCompletedValue();
    uint64_t newest = completed;
    for (const GpuSlot& slot : g_gpuSlots)
        if (slot.state == GpuState::AwaitUnity)
            newest = (std::max)(newest, slot.unityFenceValue);
    return newest > completed ? newest - completed : 0;
}

static uint64_t ReadyUnclaimedCount() {
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    uint64_t count = 0;
    for (const GpuSlot& slot : g_gpuSlots)
        if (slot.state == GpuState::Ready) ++count;
    return count;
}

static uint64_t OldestReadyAgeUs() {
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    int64_t oldest = 0;
    for (const GpuSlot& slot : g_gpuSlots) {
        if (slot.state == GpuState::Ready && slot.hostTicks > 0 &&
            (oldest == 0 || slot.hostTicks < oldest))
            oldest = slot.hostTicks;
    }
    return oldest ? QpcToMicroseconds(QpcNow() - oldest) : 0;
}

static int RequestLatestPresent(
    int32_t* slotIndex,
    uint64_t* sequence,
    int64_t* hostTicks,
    ID3D12Resource** resource) {
    if (!slotIndex || !sequence || !hostTicks || !resource) return 0;
    *slotIndex = -1;
    *sequence = 0;
    *hostTicks = 0;
    *resource = nullptr;
    if (!g_running.load(std::memory_order_acquire)) return 0;

    std::lock_guard<std::mutex> lock(g_gpuMutex);
    ReclaimUnityCompletedSlotsLocked();
    const uint64_t completed = ProducerCompletedValue();
    const uint64_t last = g_lastRequestedSequence.load(std::memory_order_relaxed);
    int newest = -1;
    uint64_t newestSequence = last;
    for (int i = 0; i < kSlotCount; ++i) {
        const GpuSlot& slot = g_gpuSlots[i];
        if (slot.state == GpuState::Ready && slot.shared12 &&
            slot.producerFenceValue <= completed &&
            slot.sequence > newestSequence) {
            newest = i;
            newestSequence = slot.sequence;
        }
    }
    if (newest < 0) return 0;
    GpuSlot& selected = g_gpuSlots[newest];
    selected.state = GpuState::PresentRequested;
    g_lastRequestedSequence.store(selected.sequence, std::memory_order_relaxed);
    *slotIndex = newest;
    *sequence = selected.sequence;
    *hostTicks = selected.hostTicks;
    *resource = selected.shared12.Get(); // borrowed; caller must not Release
    return 1;
}

static void UNITY_INTERFACE_API OnRenderEvent(int eventId) {
    if (!g_running.load(std::memory_order_acquire) ||
        eventId < kPresentEventBase || eventId >= kPresentEventBase + kSlotCount)
        return;
    const int index = eventId - kPresentEventBase;
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    GpuSlot& slot = g_gpuSlots[index];
    if (slot.state != GpuState::PresentRequested || !g_unityD3D12) return;
    slot.unityFenceValue = g_unityD3D12->GetNextFrameFenceValue();
    slot.state = GpuState::AwaitUnity;
    g_t.presentedFrame.fetch_add(1, std::memory_order_relaxed);
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType type) {
    if (type == kUnityGfxDeviceEventInitialize) {
        g_unitySupported.store(false, std::memory_order_release);
        if (!g_unityInterfaces || !g_unityGraphics ||
            g_unityGraphics->GetRenderer() != kUnityGfxRendererD3D12)
            return;
        g_unityD3D12 = g_unityInterfaces->Get<IUnityGraphicsD3D12v6>();
        if (!g_unityD3D12) return;
        g_device12 = g_unityD3D12->GetDevice();
        g_unityFrameFence = g_unityD3D12->GetFrameFence();
        if (!g_device12 || !g_unityFrameFence) return;
        UnityD3D12PluginEventConfig config{};
        config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
        config.flags = 0;
        config.ensureActiveRenderTextureIsBound = false;
        for (int i = 0; i < kSlotCount; ++i)
            g_unityD3D12->ConfigureEvent(kPresentEventBase + i, &config);
        g_unitySupported.store(true, std::memory_order_release);
    } else if (type == kUnityGfxDeviceEventShutdown) {
        {
            std::lock_guard<std::mutex> lifecycle(g_startStopMutex);
            StopUnlocked();
        }
        g_unitySupported.store(false, std::memory_order_release);
        g_unityFrameFence.Reset();
        g_device12.Reset();
        g_unityD3D12 = nullptr;
    }
}

#define KIWI_EXPORT extern "C" __declspec(dllexport)
#define KIWI_U64_EXPORT(name, expression) \
    KIWI_EXPORT uint64_t name() { return (expression); }
#define KIWI_I32_EXPORT(name, expression) \
    KIWI_EXPORT int32_t name() { return (expression); }

KIWI_U64_EXPORT(KiwiNativeCamera_GetAllSlotsBusyDropFrameCount,
    g_t.allSlotsBusyDrop.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetCaptureCopyGpuOutstandingDepth,
    g_t.captureCopyGpuOutstandingDepth.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetCaptureFrameCount,
    g_t.captureFrame.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetCaptureGpuWaitCount,
    g_t.captureGpuWait.load())
KIWI_I32_EXPORT(KiwiNativeCamera_GetCaptureTransportId,
    kSystemMemoryTransport)
KIWI_U64_EXPORT(KiwiNativeCamera_GetCpuAllSlotsBusyDropCount,
    g_t.cpuAllSlotsBusyDrop.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetCpuLatestReplacementCount,
    g_t.cpuLatestReplacement.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetDroppedFrameCount,
    g_t.dropped.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetGpuUploadCount,
    g_t.gpuUpload.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestAcceptedFrameCount,
    g_t.ingestAccepted.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestCompletedFenceValue,
    ProducerCompletedValue())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestConsumerCompletedFenceValue,
    ConsumerCompletedValue())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestConsumerFenceLag,
    ConsumerFenceLag())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestCopyFailureFrameCount,
    g_t.ingestCopyFailure.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestCopyFrameCount,
    g_t.ingestCopyFrame.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetIngestProducerFenceLag,
    ProducerFenceLag())

KIWI_EXPORT int32_t KiwiNativeCamera_GetLastErrorCode() {
    std::lock_guard<std::mutex> lock(g_errorMutex);
    return g_lastErrorCode;
}

KIWI_EXPORT const char* KiwiNativeCamera_GetLastErrorMessage() {
    std::lock_guard<std::mutex> lock(g_errorMutex);
    return g_lastErrorMessage.c_str(); // borrowed until next mutation/Stop
}

KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestArrivalIntervalMicroseconds,
    g_t.latestArrivalIntervalUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCallbackCpuMicroseconds,
    g_t.latestCallbackCpuUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCallbackToRequestNextMicroseconds,
    g_t.latestCallbackToRequestNextUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCaptureCopyGpuCompletionMicroseconds,
    g_t.latestCaptureCopyGpuCompletionUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCaptureCopyGpuSubmitMicroseconds,
    g_t.latestCaptureCopyGpuSubmitUs.load())
KIWI_EXPORT int64_t KiwiNativeCamera_GetLatestCaptureHostTicks() {
    return g_t.latestCaptureHostTicks.load();
}
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCaptureSequence,
    g_t.latestCaptureSequence.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCpuNv12CopyBytes,
    g_t.latestCpuNv12CopyBytes.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestCpuNv12CopyMicroseconds,
    g_t.latestCpuNv12CopyUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestGpuUploadSubmitMicroseconds,
    g_t.latestGpuUploadSubmitUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestIngestCopySubmitMicroseconds,
    g_t.latestIngestCopySubmitUs.load())
KIWI_EXPORT int64_t KiwiNativeCamera_GetLatestIngestHostTicks() {
    return g_t.latestIngestHostTicks.load();
}
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestIngestSequence,
    g_t.latestIngestSequence.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestMfSampleResidenceMicroseconds,
    g_t.latestMfSampleResidenceUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestProcessedSourceAgeMicroseconds,
    g_t.latestProcessedSourceAgeUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestProcessingCpuMicroseconds,
    g_t.latestProcessingCpuUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestRequestNextCpuMicroseconds,
    g_t.latestRequestNextCpuUs.load())
KIWI_EXPORT int64_t KiwiNativeCamera_GetLatestSourceHostTicks() {
    return g_t.latestSourceHostTicks.load();
}
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestSourceIntervalMicroseconds,
    g_t.latestSourceIntervalUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestSourceSequence,
    g_t.latestSourceSequence.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetLatestSourceTimestampIntervalMicroseconds,
    g_t.latestSourceTimestampIntervalUs.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetOldestReadyAgeMicroseconds,
    OldestReadyAgeUs())
KIWI_I32_EXPORT(KiwiNativeCamera_GetPresentEventBase, kPresentEventBase)
KIWI_U64_EXPORT(KiwiNativeCamera_GetPresentedFrameCount,
    g_t.presentedFrame.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetProcessingFailureFrameCount,
    g_t.processingFailure.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetProcessingGpuWaitCount,
    g_t.processingGpuWait.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetProducerCompletedFenceValue,
    ProducerCompletedValue())
KIWI_EXPORT int64_t KiwiNativeCamera_GetQpcFrequency() { return QpcFrequency(); }
KIWI_EXPORT int64_t KiwiNativeCamera_GetQpcNow() { return QpcNow(); }
KIWI_U64_EXPORT(KiwiNativeCamera_GetReadyReplacementFrameCount,
    g_t.readyReplacement.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetReadyUnclaimedCount,
    ReadyUnclaimedCount())
KIWI_EXPORT UnityRenderingEvent KiwiNativeCamera_GetRenderEventFunc() {
    return OnRenderEvent;
}
KIWI_EXPORT int32_t KiwiNativeCamera_GetSharedResourceCompatibilityTier() {
    if (!g_device12) {
        return -1;
    }

    D3D12_FEATURE_DATA_D3D12_OPTIONS4 options4{};
    const HRESULT hr = g_device12->CheckFeatureSupport(
        D3D12_FEATURE_D3D12_OPTIONS4,
        &options4,
        sizeof(options4));

    if (FAILED(hr)) {
        return -2;
    }

    return static_cast<int32_t>(
        options4.SharedResourceCompatibilityTier);
}
KIWI_U64_EXPORT(KiwiNativeCamera_GetSourceFrameCount,
    g_t.sourceFrame.load())
KIWI_U64_EXPORT(KiwiNativeCamera_GetSupersededFrameCount,
    g_t.superseded.load())
KIWI_I32_EXPORT(KiwiNativeCamera_IsCaptureD3D11MultithreadProtected,
    g_d3dMultithread ? 1 : 0)
KIWI_I32_EXPORT(KiwiNativeCamera_IsCaptureDeviceIsolated,
    g_device11 ? 1 : 0)
KIWI_I32_EXPORT(KiwiNativeCamera_IsD3D11MultithreadProtected,
    g_d3dMultithread ? 1 : 0)
KIWI_I32_EXPORT(KiwiNativeCamera_IsProcessingWorkerRunning,
    g_workerRunning.load() ? 1 : 0)
KIWI_I32_EXPORT(KiwiNativeCamera_IsRunning,
    g_running.load() ? 1 : 0)
KIWI_I32_EXPORT(KiwiNativeCamera_IsSupported,
    g_unitySupported.load() ? 1 : 0)
KIWI_I32_EXPORT(KiwiNativeCamera_IsSystemMemoryCaptureEnabled, 1)

KIWI_EXPORT int32_t KiwiNativeCamera_RequestLatestPresent(
    int32_t* slotIndex,
    uint64_t* sequence,
    int64_t* hostTicks,
    ID3D12Resource** resource) {
    return RequestLatestPresent(slotIndex, sequence, hostTicks, resource);
}

KIWI_EXPORT int32_t KiwiNativeCamera_Start(
    const wchar_t* cameraName, int32_t width, int32_t height, int32_t fps) {
    return StartInternal(cameraName, width, height, fps, kProductionMode);
}

KIWI_EXPORT int32_t KiwiNativeCamera_StartDiagnostic(
    const wchar_t* cameraName,
    int32_t width,
    int32_t height,
    int32_t fps,
    int32_t diagnosticMode) {
    if (diagnosticMode < 1 || diagnosticMode > 9) {
        SetError(E_INVALIDARG, "diagnosticMode must be in [1,9]");
        return 0;
    }
    return StartInternal(cameraName, width, height, fps, diagnosticMode);
}

KIWI_EXPORT void KiwiNativeCamera_Stop() {
    std::lock_guard<std::mutex> lifecycle(g_startStopMutex);
    StopUnlocked();
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* unityInterfaces) {
    g_unityInterfaces = unityInterfaces;
    g_unityGraphics = unityInterfaces
        ? unityInterfaces->Get<IUnityGraphics>() : nullptr;
    if (!g_unityGraphics) return;
    g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginUnload() {
    {
        std::lock_guard<std::mutex> lifecycle(g_startStopMutex);
        StopUnlocked();
    }
    if (g_unityGraphics)
        g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_unitySupported.store(false, std::memory_order_release);
    g_unityFrameFence.Reset();
    g_device12.Reset();
    g_unityD3D12 = nullptr;
    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
}

#undef KIWI_I32_EXPORT
#undef KIWI_U64_EXPORT
#undef KIWI_EXPORT

} // namespace kiwi_camera_candidate
