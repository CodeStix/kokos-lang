// Source for native_fixture.dll, checked into the repo prebuilt so KokosJitTests.LoadLibrary tests
// don't depend on a C toolchain being available at `dotnet test` time. Rebuild with (from a Visual
// Studio Developer Command Prompt, x64):
//
//   cl.exe /nologo /LD native_fixture.c /Fe:native_fixture.dll
//
// and delete the resulting .lib/.exp/.obj — only native_fixture.dll is a checked-in test asset.
__declspec(dllexport) long long kokos_test_triple(long long x)
{
    return x * 3;
}
