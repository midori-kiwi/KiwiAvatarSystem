#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <cstdio>
#include <fstream>
#include <iterator>
#include <vector>

#pragma comment(lib, "d3dcompiler.lib")

using Microsoft::WRL::ComPtr;

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::fwprintf(stderr, L"Usage: KiwiHlslCompileProbe.exe <shader.hlsl>\n");
        return 2;
    }

    std::ifstream input(argv[1], std::ios::binary);
    if (!input)
    {
        std::fwprintf(stderr, L"Cannot open shader: %ls\n", argv[1]);
        return 3;
    }

    std::vector<char> source(
        (std::istreambuf_iterator<char>(input)),
        std::istreambuf_iterator<char>());

    if (source.empty())
    {
        std::fprintf(stderr, "Shader source is empty.\n");
        return 4;
    }

    ComPtr<ID3DBlob> code;
    ComPtr<ID3DBlob> errors;

    const HRESULT hr = D3DCompile(
        source.data(),
        source.size(),
        "KiwiNv12ToRgba",
        nullptr,
        nullptr,
        "CSMain",
        "cs_5_0",
        D3DCOMPILE_OPTIMIZATION_LEVEL3,
        0,
        &code,
        &errors);

    std::printf("HRESULT=0x%08X\n", static_cast<unsigned>(hr));
    std::printf("SourceBytes=%zu\n", source.size());
    std::printf("BytecodeBytes=%zu\n", code ? code->GetBufferSize() : 0U);

    if (errors && errors->GetBufferPointer() && errors->GetBufferSize())
    {
        std::printf(
            "CompilerMessages=%.*s\n",
            static_cast<int>(errors->GetBufferSize()),
            static_cast<const char*>(errors->GetBufferPointer()));
    }
    else
    {
        std::printf("CompilerMessages=\n");
    }

    return SUCCEEDED(hr) && code ? 0 : 1;
}
