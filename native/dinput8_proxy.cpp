#define WIN32_LEAN_AND_MEAN
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <initguid.h>
#include <dinput.h>
#include <stdint.h>
#include <math.h>
#include <float.h>
#include <new>
#include <string.h>
#include <stdio.h>

// Shared with Tweak.Runtime.  Keep this wire format packed and pointer-free.
struct Point { double x, y; };
struct ConfigWire {
    uint32_t magic, version, sequence, flags;
    GUID selectedGuid;
    uint32_t axis, leftCount, rightCount, reserved;
    double minimum, center, maximum, deadzone, saturation;
    uint64_t reserved2;
    Point left[32];
    Point right[32];
};
struct TelemetryWire {
    uint32_t magic, version, sequence, pid, status, configSequence;
    uint64_t sampleCount;
    double input, output;
    uint64_t tickCount;
    uint32_t flags;
};
static_assert(sizeof(ConfigWire) == 1120, "wire layout mismatch");
static_assert(offsetof(TelemetryWire, sampleCount) == 24, "telemetry layout mismatch");

static constexpr uint32_t kMagic = 0x31575443;
static constexpr uint32_t kEnabled = 1, kBypass = 2, kMonitor = 4, kInvert = 8, kMirror = 16;
static HMODULE g_realModule;
static HANDLE g_configMap, g_telemetryMap, g_worker;
static const ConfigWire* g_configView;
static TelemetryWire* g_telemetry;
static SRWLOCK g_cacheLock = SRWLOCK_INIT;
static SRWLOCK g_telemetryLock = SRWLOCK_INIT;
static ConfigWire g_cached = {};
static volatile LONG g_status = 0;
static volatile LONG g_init = 0;
static volatile LONG g_diag = 0;
static wchar_t g_telemetryName[96];
static wchar_t g_configName[256]=L"Local\\ControllerTweak.Config.v1";
static void InitializeProxy();

static bool finite(double x) { return _finite(x) != 0; }

static bool ValidateConfig(const ConfigWire& c) {
    if (c.magic != kMagic || c.version != 1 || (c.sequence & 1) || c.axis > 7 ||
        c.leftCount < 2 || c.leftCount > 32 || c.rightCount < 2 || c.rightCount > 32 ||
        !finite(c.minimum) || !finite(c.center) || !finite(c.maximum) ||
        !finite(c.deadzone) || !finite(c.saturation) ||
        !(c.minimum < c.center && c.center < c.maximum) || c.minimum < -1 || c.maximum > 1 ||
        c.deadzone < 0 || c.deadzone >= c.saturation || c.saturation > 1) return false;
    for (uint32_t side = 0; side < 2; ++side) {
        const Point* p = side ? c.right : c.left;
        uint32_t n = side ? c.rightCount : c.leftCount;
        if (p[0].x != 0 || p[0].y != 0 || p[n-1].x != 1 || p[n-1].y != 1) return false;
        for (uint32_t i = 0; i < n; ++i) {
            if (!finite(p[i].x) || !finite(p[i].y) || p[i].x < 0 || p[i].x > 1 || p[i].y < 0 || p[i].y > 1) return false;
            if (i && (p[i].x <= p[i-1].x || p[i].y < p[i-1].y)) return false;
        }
    }
    return true;
}

static double Curve(double x, const Point* p, uint32_t n) {
    if (x <= p[0].x) return p[0].y;
    if (x >= p[n-1].x) return p[n-1].y;
    uint32_t lo = 0, hi = n - 1;
    while (hi - lo > 1) { uint32_t m = lo + (hi - lo) / 2; if (x < p[m].x) hi = m; else lo = m; }
    double t = (x - p[lo].x) / (p[hi].x - p[lo].x);
    return p[lo].y + t * (p[hi].y - p[lo].y);
}

static double Apply(double input, const ConfigWire& c) {
    if (!finite(input)) return 0;
    double delta = input - c.center;
    bool positive = delta >= 0;
    double range = positive ? c.maximum - c.center : c.center - c.minimum;
    if (!(range > 0)) return 0;
    double magnitude = fabs(delta) / range;
    if (magnitude >= 1) magnitude = 1;
    if (magnitude <= c.deadzone) magnitude = 0;
    else if (magnitude >= c.saturation) magnitude = 1;
    else magnitude = (magnitude - c.deadzone) / (c.saturation - c.deadzone);
    const Point* curve = (c.flags & kMirror) || positive ? c.right : c.left;
    double output = Curve(magnitude, curve, (curve == c.left) ? c.leftCount : c.rightCount);
    if (!positive) output = -output;
    if (c.flags & kInvert) output = -output;
    return output;
}

static void PublishStatus(uint32_t status) {
    LONG previous=InterlockedExchange(&g_status, status);
    if (previous==static_cast<LONG>(status)) return;
    if (!g_telemetry) return;
    AcquireSRWLockExclusive(&g_telemetryLock);
    InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
    g_telemetry->status = status;
    g_telemetry->pid = GetCurrentProcessId();
    InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
    ReleaseSRWLockExclusive(&g_telemetryLock);
}
static void PublishConfigSequence(uint32_t sequence) {
    if (!g_telemetry || g_telemetry->configSequence==sequence) return;
    AcquireSRWLockExclusive(&g_telemetryLock);
    if (g_telemetry->configSequence!=sequence) {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
        g_telemetry->configSequence=sequence;
        g_telemetry->status=1;
        g_telemetry->input=g_telemetry->output=0;
        g_telemetry->tickCount=0;
        InterlockedExchange(&g_status,1);
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
    }
    ReleaseSRWLockExclusive(&g_telemetryLock);
}

static bool ReadConfig(ConfigWire& out) {
    if (!g_configView) return false;
    for (int attempt = 0; attempt < 3; ++attempt) {
        uint32_t before = *reinterpret_cast<const volatile uint32_t*>(&g_configView->sequence);
        if (before & 1) continue;
        MemoryBarrier();
        memcpy(&out, g_configView, sizeof(out));
        MemoryBarrier();
        uint32_t after = *reinterpret_cast<const volatile uint32_t*>(&g_configView->sequence);
        if (before == after && !(after & 1) && ValidateConfig(out)) return true;
    }
    return false;
}

static DWORD WINAPI ConfigWorker(void*) {
    ConfigWire next = {};
    for (;;) {
        if (!g_configView) {
            if (!g_configMap) g_configMap = OpenFileMappingW(FILE_MAP_READ,FALSE,g_configName);
            if (g_configMap) g_configView = static_cast<const ConfigWire*>(MapViewOfFile(g_configMap,FILE_MAP_READ,0,0,4096));
        }
        if (ReadConfig(next)) {
            AcquireSRWLockExclusive(&g_cacheLock);
            bool changed=g_cached.sequence!=next.sequence;
            g_cached = next;
            if (changed) PublishConfigSequence(next.sequence);
            ReleaseSRWLockExclusive(&g_cacheLock);
        }
        Sleep(20);
    }
}

static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK InitializeOnce(PINIT_ONCE, PVOID, PVOID*) {
    InterlockedExchange(&g_init, 1);
    // Pin once initialized so worker callbacks and shadow vtables cannot outlive this module.
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&InitializeProxy), &pinned);
    wchar_t sys[MAX_PATH];
    UINT n = GetSystemDirectoryW(sys, MAX_PATH);
    if (n && n < MAX_PATH - 16) {
        wcscat_s(sys, MAX_PATH, L"\\dinput8.dll");
        g_realModule = LoadLibraryW(sys);
    }
    g_cached.magic = kMagic; g_cached.version = 1; g_cached.flags = kBypass;
    g_cached.minimum = -1; g_cached.center = 0; g_cached.maximum = 1; g_cached.saturation = 1;
    g_cached.leftCount = g_cached.rightCount = 2;
    g_cached.left[0] = g_cached.right[0] = {0,0};
    g_cached.left[1] = g_cached.right[1] = {1,1};
    // An explicit namespace lets integration tests isolate their editor and child
    // process from any normal game/editor session. Ordinary launches use the default.
    wchar_t testNamespace[200];
    DWORD namespaceLength=GetEnvironmentVariableW(L"CONTROLLER_TWEAK_IPC_NAMESPACE",testNamespace,_countof(testNamespace));
    if (namespaceLength>0 && namespaceLength<_countof(testNamespace))
        _snwprintf_s(g_configName,_countof(g_configName),_TRUNCATE,L"%s.Config.v1",testNamespace);
    g_configMap = OpenFileMappingW(FILE_MAP_READ, FALSE, g_configName);
    if (g_configMap) g_configView = static_cast<const ConfigWire*>(MapViewOfFile(g_configMap, FILE_MAP_READ, 0, 0, 4096));
    _snwprintf_s(g_telemetryName, _countof(g_telemetryName), _TRUNCATE, L"Local\\ControllerTweak.Telemetry.v1.%lu", GetCurrentProcessId());
    g_telemetryMap = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 4096, g_telemetryName);
    if (g_telemetryMap) {
        g_telemetry = static_cast<TelemetryWire*>(MapViewOfFile(g_telemetryMap, FILE_MAP_ALL_ACCESS, 0, 0, 4096));
        if (g_telemetry && GetLastError() != ERROR_ALREADY_EXISTS) ZeroMemory(g_telemetry, 4096);
        if (g_telemetry) { g_telemetry->magic = kMagic; g_telemetry->version = 1; }
    }
    g_worker = CreateThread(nullptr, 0, ConfigWorker, nullptr, 0, nullptr);
    PublishStatus(1);
    return TRUE;
}
static void InitializeProxy() {
    InitOnceExecuteOnce(&g_once, InitializeOnce, nullptr, nullptr);
}

// COM shadow wrapper: each proxy owns its vtable copy. Calls go to the original COM object,
// so device methods still enter any Competition Patch detours installed on Microsoft's vtable.
struct Proxy {
    void** vtable;
    BYTE* stubs;
    void* raw;
    void** rawVtable;
    volatile LONG refs;
    bool root;
    bool wide;
    GUID instance;
    DWORD tableSize;
    DWORD axisOffsets[8];
    DWORD axisObjectIds[8];
    DWORD axisCount;
    bool axisPresent[8];
    bool rangeKnown[8];
    DWORD axisOffset;
    LONG axisMin[8], axisMax[8];
    bool formatKnown, selectedAxisKnown;
    bool rawCalibration, relativeMode;
    Proxy* identity;
};
static_assert(offsetof(Proxy,raw)==8 && offsetof(Proxy,rawVtable)==12, "x86 dispatch offsets changed");
static HRESULT WINAPI RootCreateDevice(void*, REFGUID, void**, IUnknown*);
static HRESULT WINAPI RootEnumDevicesBySemanticsA(void*, LPCSTR, LPDIACTIONFORMATA, LPDIENUMDEVICESBYSEMANTICSCB, LPVOID, DWORD);
static HRESULT WINAPI RootEnumDevicesBySemanticsW(void*, LPCWSTR, LPDIACTIONFORMATW, LPDIENUMDEVICESBYSEMANTICSCB, LPVOID, DWORD);
static void __declspec(naked) ForwardDispatch() {
    __asm {
        mov ecx, dword ptr [esp]
        mov eax, dword ptr [esp+8]
        add esp, 4
        mov edx, dword ptr [eax+8]
        mov dword ptr [esp+4], edx
        mov eax, dword ptr [eax+12]
        mov eax, dword ptr [eax+ecx*4]
        jmp eax
    }
}
static void ReportUnsupported() { if (InterlockedExchange(&g_diag, 1) == 0) PublishStatus(4); }

static bool DeviceIidEncoding(REFIID id, bool& wide, DWORD& slots) {
    if (IsEqualIID(id,IID_IDirectInputDevice8W)) { wide=true; slots=32; return true; }
    if (IsEqualIID(id,IID_IDirectInputDevice8A)) { wide=false; slots=32; return true; }
    if (IsEqualIID(id,IID_IDirectInputDevice7W)) { wide=true; slots=29; return true; }
    if (IsEqualIID(id,IID_IDirectInputDevice7A)) { wide=false; slots=29; return true; }
    if (IsEqualIID(id,IID_IDirectInputDevice2W)) { wide=true; slots=27; return true; }
    if (IsEqualIID(id,IID_IDirectInputDevice2A)) { wide=false; slots=27; return true; }
    if (IsEqualIID(id,IID_IDirectInputDeviceW)) { wide=true; slots=18; return true; }
    if (IsEqualIID(id,IID_IDirectInputDeviceA)) { wide=false; slots=18; return true; }
    return false;
}
static Proxy* NewProxy(void*, bool, bool, REFGUID, DWORD);
static ULONG WINAPI ProxyAddRef(void* self);
static ULONG WINAPI ProxyRelease(void* self);

static HRESULT WINAPI ProxyQueryInterface(void* self, REFIID iid, void** out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    Proxy* p = static_cast<Proxy*>(self);
    if (IsEqualIID(iid,IID_IUnknown)) {
        void* queried=nullptr;
        HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,REFIID,void**)>(p->rawVtable[0])(p->raw,iid,&queried);
        if (FAILED(hr)) return hr;
        if (queried) reinterpret_cast<ULONG(WINAPI*)(void*)>((*reinterpret_cast<void***>(queried))[2])(queried);
        *out=p->identity; ProxyAddRef(p->identity); return S_OK;
    }
    bool targetWide=p->wide, supported=false;
    DWORD targetSlots=32;
    IID targetIid=iid;
    if (p->root) {
        if (IsEqualIID(iid,IID_IDirectInput8W) || IsEqualIID(iid,IID_IDirectInput8A)) { supported=true; targetWide=IsEqualIID(iid,IID_IDirectInput8W); }
    } else supported=DeviceIidEncoding(iid,targetWide,targetSlots);
    if (supported && targetWide==p->identity->wide && targetWide!=p->wide) {
        void* queried=nullptr;
        HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,REFIID,void**)>(p->rawVtable[0])(p->raw,iid,&queried);
        if (FAILED(hr)) return hr;
        if (queried) reinterpret_cast<ULONG(WINAPI*)(void*)>((*reinterpret_cast<void***>(queried))[2])(queried);
        ProxyAddRef(p->identity); *out=p->identity; return S_OK;
    }
    if (supported && targetWide==p->wide && (p->root || targetSlots<=p->tableSize)) {
        void* queried=nullptr;
        HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,REFIID,void**)>(p->rawVtable[0])(p->raw,iid,&queried);
        if (FAILED(hr)) return hr;
        if (queried) reinterpret_cast<ULONG(WINAPI*)(void*)>((*reinterpret_cast<void***>(queried))[2])(queried);
        ProxyAddRef(p); *out=p; return S_OK;
    }
    if (supported && !p->root) {
        void* queried=nullptr;
        HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,REFIID,void**)>(p->rawVtable[0])(p->raw,targetIid,&queried);
        if (FAILED(hr)) return hr;
        Proxy* child=NewProxy(queried,false,targetWide,p->instance,targetSlots);
        if (!child) { reinterpret_cast<ULONG(WINAPI*)(void*)>((*reinterpret_cast<void***>(queried))[2])(queried); return E_OUTOFMEMORY; }
        child->identity=p->identity; ProxyAddRef(child->identity); *out=child; return S_OK;
    }
    if (supported && p->root) {
        targetIid=targetWide ? IID_IDirectInput8W : IID_IDirectInput8A;
        void* queried=nullptr;
        HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,REFIID,void**)>(p->rawVtable[0])(p->raw,targetIid,&queried);
        if (FAILED(hr)) return hr;
        GUID zero={}; Proxy* child=NewProxy(queried,true,targetWide,zero,11);
        if (!child) { reinterpret_cast<ULONG(WINAPI*)(void*)>((*reinterpret_cast<void***>(queried))[2])(queried); return E_OUTOFMEMORY; }
        child->identity=p->identity; ProxyAddRef(child->identity); *out=child; return S_OK;
    }
    void* passthrough=nullptr;
    HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*, REFIID, void**)>(p->rawVtable[0])(p->raw, iid, &passthrough);
    if (SUCCEEDED(hr) && passthrough) { ReportUnsupported(); *out=passthrough; }
    return hr;
}
static ULONG WINAPI ProxyAddRef(void* self) { return InterlockedIncrement(&static_cast<Proxy*>(self)->refs); }
static ULONG WINAPI ProxyRelease(void* self) {
    Proxy* p = static_cast<Proxy*>(self);
    LONG left = InterlockedDecrement(&p->refs);
    if (!left) {
        Proxy* identity=p->identity;
        reinterpret_cast<ULONG (WINAPI*)(void*)>(p->rawVtable[2])(p->raw);
        VirtualFree(p->stubs,0,MEM_RELEASE);
        delete[] p->vtable;
        delete p;
        if (identity && identity!=p) ProxyRelease(identity);
    }
    return left;
}

static bool DeviceMatches(Proxy* p, ConfigWire& c) {
    p=p->identity;
    AcquireSRWLockShared(&g_cacheLock); c = g_cached; ReleaseSRWLockShared(&g_cacheLock);
    if (!ValidateConfig(c) || !IsEqualGUID(p->instance,c.selectedGuid)) return false;
    if (c.axis >= p->axisCount || !p->axisPresent[c.axis] || !p->rangeKnown[c.axis] ||
        !p->formatKnown || p->rawCalibration || p->relativeMode) {
        PublishStatus(4);
        return false;
    }
    p->axisOffset=p->axisOffsets[c.axis];
    p->selectedAxisKnown=true;
    return true;
}
static int AxisFromGuid(REFGUID id, DWORD& sliderOrdinal) {
    if (IsEqualGUID(id,GUID_XAxis)) return 0;
    if (IsEqualGUID(id,GUID_YAxis)) return 1;
    if (IsEqualGUID(id,GUID_ZAxis)) return 2;
    if (IsEqualGUID(id,GUID_RxAxis)) return 3;
    if (IsEqualGUID(id,GUID_RyAxis)) return 4;
    if (IsEqualGUID(id,GUID_RzAxis)) return 5;
    if (IsEqualGUID(id,GUID_Slider)) return sliderOrdinal < 2 ? 6 + static_cast<int>(sliderOrdinal++) : -1;
    return -1;
}
static double GetAxisValue(LONG value, LONG min, LONG max) {
    if (max <= min) return 0;
    return -1.0 + (2.0 * ((double)value - min) / ((double)max - min));
}
static LONG SetAxisValue(double value, LONG min, LONG max) {
    if (value < -1) value = -1; if (value > 1) value = 1;
    return (LONG)floor((value + 1.0) * 0.5 * ((double)max - min) + min + 0.5);
}
static void Sample(const ConfigWire& c, double input, double output) {
    if (!(c.flags & kMonitor) || !g_telemetry) return;
    AcquireSRWLockExclusive(&g_telemetryLock);
    if (g_telemetry->configSequence!=c.sequence) { ReleaseSRWLockExclusive(&g_telemetryLock); return; }
    InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
    g_telemetry->configSequence = c.sequence;
    ++g_telemetry->sampleCount;
    g_telemetry->input = input; g_telemetry->output = output;
    g_telemetry->tickCount = GetTickCount64();
    g_telemetry->status = 3;
    InterlockedExchange(&g_status,3);
    InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
    ReleaseSRWLockExclusive(&g_telemetryLock);
}
static void PublishReadStatus(const ConfigWire& c) {
    // Only state transitions publish when preview is off; no per-sample IPC.
    if (!g_telemetry || InterlockedCompareExchange(&g_status,3,3)==3) return;
    AcquireSRWLockExclusive(&g_telemetryLock);
    if (g_telemetry->configSequence==c.sequence) {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
        g_telemetry->status=3;
        InterlockedExchange(&g_status,3);
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&g_telemetry->sequence));
    }
    ReleaseSRWLockExclusive(&g_telemetryLock);
}

static HRESULT WINAPI DeviceGetState(void* self, DWORD size, LPVOID data) {
    Proxy* p = static_cast<Proxy*>(self);
    HRESULT hr = reinterpret_cast<HRESULT (WINAPI*)(void*,DWORD,LPVOID)>(p->rawVtable[9])(p->raw,size,data);
    Proxy* state=p->identity;
    ConfigWire c;
    if (hr==DIERR_INPUTLOST || hr==DIERR_NOTACQUIRED || hr==static_cast<HRESULT>(DIERR_UNPLUGGED)) {
        if (DeviceMatches(p,c)) PublishStatus(5);
    }
    if (SUCCEEDED(hr) && data && DeviceMatches(p,c) && state->axisOffset <= size && sizeof(LONG) <= size-state->axisOffset) {
        LONG* value = reinterpret_cast<LONG*>(static_cast<BYTE*>(data)+state->axisOffset);
        LONG minimum=state->axisMin[c.axis], maximum=state->axisMax[c.axis];
        double in = GetAxisValue(*value,minimum,maximum), out = in;
        if ((c.flags & kEnabled) && !(c.flags & kBypass)) { out=Apply(in,c); *value=SetAxisValue(out,minimum,maximum); }
        PublishReadStatus(c);
        Sample(c,in,out);
    }
    return hr;
}
static HRESULT WINAPI DeviceGetData(void* self, DWORD cb, LPDIDEVICEOBJECTDATA data, LPDWORD count, DWORD flags) {
    Proxy* p = static_cast<Proxy*>(self);
    HRESULT hr = reinterpret_cast<HRESULT (WINAPI*)(void*,DWORD,LPDIDEVICEOBJECTDATA,LPDWORD,DWORD)>(p->rawVtable[10])(p->raw,cb,data,count,flags);
    Proxy* state=p->identity;
    ConfigWire c;
    if (hr==DIERR_INPUTLOST || hr==DIERR_NOTACQUIRED || hr==static_cast<HRESULT>(DIERR_UNPLUGGED)) {
        if (DeviceMatches(p,c)) PublishStatus(5);
    }
    if (SUCCEEDED(hr) && data && count && DeviceMatches(p,c)) {
        DWORD n = *count;
        if (cb < sizeof(DWORD)*2) n = 0;
        BYTE* bytes = reinterpret_cast<BYTE*>(data);
        for (DWORD i=0; i<n; ++i) {
            BYTE* entry = bytes + static_cast<size_t>(i)*cb;
            DWORD offset, rawData;
            memcpy(&offset,entry,sizeof(offset)); memcpy(&rawData,entry+sizeof(offset),sizeof(rawData));
            if (offset != state->axisOffset) continue;
            LONG raw = (LONG)rawData;
            LONG minimum=state->axisMin[c.axis], maximum=state->axisMax[c.axis];
            double in = GetAxisValue(raw,minimum,maximum), out = in;
            if ((c.flags & kEnabled) && !(c.flags & kBypass)) {
                out=Apply(in,c);
                DWORD transformed=(DWORD)SetAxisValue(out,minimum,maximum);
                memcpy(entry+sizeof(offset),&transformed,sizeof(transformed));
            }
            PublishReadStatus(c);
            Sample(c,in,out);
        }
    }
    return hr;
}
static HRESULT WINAPI DeviceSetDataFormat(void* self, LPCDIDATAFORMAT format) {
    Proxy* p = static_cast<Proxy*>(self);
    HRESULT hr = reinterpret_cast<HRESULT (WINAPI*)(void*,LPCDIDATAFORMAT)>(p->rawVtable[11])(p->raw,format);
    if (SUCCEEDED(hr)) {
        Proxy* state=p->identity;
        state->relativeMode=(format && (format->dwFlags&DIDF_RELAXIS))!=0;
        state->formatKnown = false; state->selectedAxisKnown = false;
        state->axisCount = 0;
        ZeroMemory(state->axisPresent,sizeof(state->axisPresent));
        ZeroMemory(state->rangeKnown,sizeof(state->rangeKnown));
        if (format && (format->dwFlags & DIDF_ABSAXIS) && format->rgodf && format->dwObjSize >= sizeof(DIOBJECTDATAFORMAT)) {
            const BYTE* raw = reinterpret_cast<const BYTE*>(format->rgodf);
            DWORD sliderOrdinal=0;
            for (DWORD i=0; i<format->dwNumObjs; ++i) {
                const DIOBJECTDATAFORMAT& o = *reinterpret_cast<const DIOBJECTDATAFORMAT*>(raw + i * format->dwObjSize);
                if (!(o.dwType & DIDFT_AXIS)) continue;
                // DIJOYSTATE2 repeats X/Y/etc. for velocity, acceleration and
                // force. Only position belongs to our steering axis mapping.
                const DWORD aspect=o.dwFlags&DIDOI_ASPECTMASK;
                if (aspect && aspect!=DIDOI_ASPECTPOSITION) continue;
                int semantic=o.pguid ? AxisFromGuid(*o.pguid,sliderOrdinal) : -1;
                DWORD objectId=o.dwType;
                if (p->wide) {
                    DIDEVICEOBJECTINSTANCEW info={}; info.dwSize=sizeof(info);
                    HRESULT infoHr=reinterpret_cast<HRESULT (WINAPI*)(void*,LPDIDEVICEOBJECTINSTANCEW,DWORD,DWORD)>(p->rawVtable[14])(p->raw,&info,o.dwOfs,DIPH_BYOFFSET);
                    if (SUCCEEDED(infoHr)) { objectId=info.dwType; if (semantic<0) semantic=AxisFromGuid(info.guidType,sliderOrdinal); }
                } else {
                    DIDEVICEOBJECTINSTANCEA info={}; info.dwSize=sizeof(info);
                    HRESULT infoHr=reinterpret_cast<HRESULT (WINAPI*)(void*,LPDIDEVICEOBJECTINSTANCEA,DWORD,DWORD)>(p->rawVtable[14])(p->raw,&info,o.dwOfs,DIPH_BYOFFSET);
                    if (SUCCEEDED(infoHr)) { objectId=info.dwType; if (semantic<0) semantic=AxisFromGuid(info.guidType,sliderOrdinal); }
                }
                if (semantic>=0 && semantic<8) { DWORD axis=static_cast<DWORD>(semantic); state->axisOffsets[axis]=o.dwOfs; state->axisObjectIds[axis]=objectId; state->axisPresent[axis]=true; if (state->axisCount<=axis) state->axisCount=axis+1; }
            }
            ConfigWire c; AcquireSRWLockShared(&g_cacheLock); c=g_cached; ReleaseSRWLockShared(&g_cacheLock);
            if (state->axisCount) state->formatKnown=true;
            if (c.axis < state->axisCount && state->axisPresent[c.axis]) { state->axisOffset=state->axisOffsets[c.axis]; state->selectedAxisKnown=true; }
            // Cache every absolute axis range now. GetDeviceState stays allocation- and IPC-free.
            for (DWORD axis=0;axis<state->axisCount;++axis) if (state->axisPresent[axis]) {
                DIPROPRANGE range = {};
                range.diph.dwSize=sizeof(range); range.diph.dwHeaderSize=sizeof(range.diph); range.diph.dwObj=state->axisOffsets[axis]; range.diph.dwHow=DIPH_BYOFFSET;
                HRESULT phr = reinterpret_cast<HRESULT (WINAPI*)(void*,REFGUID,LPDIPROPHEADER)>(p->rawVtable[5])(p->raw,DIPROP_RANGE,&range.diph);
                if (SUCCEEDED(phr) && range.lMax > range.lMin) { state->axisMin[axis]=range.lMin; state->axisMax[axis]=range.lMax; state->rangeKnown[axis]=true; }
            }
        }
        if (!p->identity->formatKnown) ReportUnsupported();
    }
    return hr;
}
static HRESULT WINAPI DeviceSetProperty(void* self, REFGUID prop, LPCDIPROPHEADER header) {
    Proxy* p = static_cast<Proxy*>(self);
    HRESULT hr = reinterpret_cast<HRESULT (WINAPI*)(void*,REFGUID,LPCDIPROPHEADER)>(p->rawVtable[6])(p->raw,prop,header);
    Proxy* state=p->identity;
    if (SUCCEEDED(hr) && header && header->dwSize>=sizeof(DIPROPDWORD)) {
        uintptr_t property=reinterpret_cast<uintptr_t>(&prop);
        const auto value=reinterpret_cast<const DIPROPDWORD*>(header)->dwData;
        if (property==10) {
            if (value==DIPROPCALIBRATIONMODE_RAW) { state->rawCalibration=true; ReportUnsupported(); }
            else if (header->dwHow==DIPH_DEVICE) state->rawCalibration=false;
        }
        if (property==2) {
            state->relativeMode=value==DIPROPAXISMODE_REL;
            if (state->relativeMode) ReportUnsupported();
        }
    }
    if (SUCCEEDED(hr) && reinterpret_cast<uintptr_t>(&prop)==4 && header && header->dwSize >= sizeof(DIPROPRANGE)) {
        const DIPROPRANGE* range = reinterpret_cast<const DIPROPRANGE*>(header);
        if (header->dwHow == DIPH_BYOFFSET && state->formatKnown) {
            for (DWORD i=0;i<state->axisCount;++i) if (state->axisPresent[i] && header->dwObj==state->axisOffsets[i]) { state->axisMin[i]=range->lMin; state->axisMax[i]=range->lMax; state->rangeKnown[i]=true; }
        } else if (header->dwHow == DIPH_BYID) {
            for (DWORD i=0;i<state->axisCount;++i) if (state->axisPresent[i] && header->dwObj==state->axisObjectIds[i]) { state->axisMin[i]=range->lMin; state->axisMax[i]=range->lMax; state->rangeKnown[i]=true; }
        } else if (header->dwHow == DIPH_DEVICE) { for (DWORD i=0;i<state->axisCount;++i) if (state->axisPresent[i]) { state->axisMin[i]=range->lMin; state->axisMax[i]=range->lMax; state->rangeKnown[i]=true; } }
    }
    return hr;
}
static HRESULT WINAPI DeviceGetProperty(void* self, REFGUID prop, LPDIPROPHEADER header) {
    Proxy* p = static_cast<Proxy*>(self);
    HRESULT hr = reinterpret_cast<HRESULT (WINAPI*)(void*,REFGUID,LPDIPROPHEADER)>(p->rawVtable[5])(p->raw,prop,header);
    Proxy* state=p->identity;
    if (SUCCEEDED(hr) && reinterpret_cast<uintptr_t>(&prop)==4 && header && header->dwSize >= sizeof(DIPROPRANGE) && state->selectedAxisKnown) {
        DIPROPRANGE* range = reinterpret_cast<DIPROPRANGE*>(header);
        if (header->dwHow == DIPH_DEVICE) { for (DWORD i=0;i<state->axisCount;++i) if (state->axisPresent[i]) { state->axisMin[i]=range->lMin; state->axisMax[i]=range->lMax; state->rangeKnown[i]=true; } }
        else if (header->dwHow == DIPH_BYOFFSET) { for (DWORD i=0;i<state->axisCount;++i) if (state->axisPresent[i] && header->dwObj==state->axisOffsets[i]) { state->axisMin[i]=range->lMin; state->axisMax[i]=range->lMax; state->rangeKnown[i]=true; } }
        else if (header->dwHow == DIPH_BYID) { for (DWORD i=0;i<state->axisCount;++i) if (state->axisPresent[i] && header->dwObj==state->axisObjectIds[i]) { state->axisMin[i]=range->lMin; state->axisMax[i]=range->lMax; state->rangeKnown[i]=true; } }
    }
    return hr;
}
static HRESULT WINAPI DeviceSetActionMap(void* self, LPDIACTIONFORMATW format, LPCWSTR user, DWORD flags) {
    Proxy* p=static_cast<Proxy*>(self);
    HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,LPDIACTIONFORMATW,LPCWSTR,DWORD)>(p->rawVtable[30])(p->raw,format,user,flags);
    if (SUCCEEDED(hr)) { p->identity->formatKnown=false; p->identity->selectedAxisKnown=false; ReportUnsupported(); }
    return hr;
}

static Proxy* NewProxy(void* raw, bool root, bool wide, REFGUID instance, DWORD slots) {
    if (!raw) return nullptr;
    Proxy* p = new (std::nothrow) Proxy{};
    if (!p) return nullptr;
    p->raw=raw; p->rawVtable=*reinterpret_cast<void***>(raw); p->refs=1; p->root=root; p->wide=wide; p->instance=instance; p->tableSize=slots;
    p->identity=p;
    p->vtable=new (std::nothrow) void*[slots];
    if (!p->vtable) { delete p; return nullptr; }
    p->stubs=static_cast<BYTE*>(VirtualAlloc(nullptr,slots*16,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!p->stubs) { delete[] p->vtable; delete p; return nullptr; }
    memcpy(p->vtable,p->rawVtable,slots*sizeof(void*));
    for (DWORD i=0;i<slots;++i) {
        BYTE* s=p->stubs+i*16;
        s[0]=0x68;memcpy(s+1,&i,4);
        s[5]=0xe9;
        intptr_t rel=reinterpret_cast<BYTE*>(&ForwardDispatch)-(s+10);
        int32_t rel32=static_cast<int32_t>(rel);memcpy(s+6,&rel32,4);
        p->vtable[i]=s;
    }
    DWORD previousProtection;
    if (!VirtualProtect(p->stubs,slots*16,PAGE_EXECUTE_READ,&previousProtection) ||
        !FlushInstructionCache(GetCurrentProcess(),p->stubs,slots*16)) {
        VirtualFree(p->stubs,0,MEM_RELEASE); delete[] p->vtable; delete p; return nullptr;
    }
    p->vtable[0]=reinterpret_cast<void*>(ProxyQueryInterface); p->vtable[1]=reinterpret_cast<void*>(ProxyAddRef); p->vtable[2]=reinterpret_cast<void*>(ProxyRelease);
    if (root) {
        p->vtable[3]=reinterpret_cast<void*>(RootCreateDevice);
        p->vtable[9]=wide ? reinterpret_cast<void*>(RootEnumDevicesBySemanticsW) : reinterpret_cast<void*>(RootEnumDevicesBySemanticsA);
    }
    else {
        p->vtable[5]=reinterpret_cast<void*>(DeviceGetProperty); p->vtable[6]=reinterpret_cast<void*>(DeviceSetProperty);
        p->vtable[9]=reinterpret_cast<void*>(DeviceGetState); p->vtable[10]=reinterpret_cast<void*>(DeviceGetData);
        p->vtable[11]=reinterpret_cast<void*>(DeviceSetDataFormat);
        if (slots > 30) p->vtable[30]=reinterpret_cast<void*>(DeviceSetActionMap);
    }
    return p;
}
static void WrapDevice(void* raw, REFGUID instance, bool wide, void** out) {
    if (!raw || !out) return;
    if (IsEqualGUID(instance,GUID_SysKeyboard) || IsEqualGUID(instance,GUID_SysMouse)) { *out=raw; return; }
    Proxy* p=NewProxy(raw,false,wide,instance,32);
    if (!p) { *out=raw; return; }
    *out=p;
}

static HRESULT WINAPI RootCreateDevice(void* self, REFGUID instance, void** out, IUnknown* outer) {
    Proxy* p=static_cast<Proxy*>(self);
    HRESULT hr=reinterpret_cast<HRESULT (WINAPI*)(void*,REFGUID,void**,IUnknown*)>(p->rawVtable[3])(p->raw,instance,out,outer);
    if (SUCCEEDED(hr) && out && *out && !outer) WrapDevice(*out,instance,p->wide,out);
    return hr;
}
static HRESULT WINAPI RootEnumDevicesBySemanticsA(void* self, LPCSTR user, LPDIACTIONFORMATA format, LPDIENUMDEVICESBYSEMANTICSCB callback, LPVOID ref, DWORD flags) {
    Proxy* p=static_cast<Proxy*>(self);
    HRESULT hr=reinterpret_cast<HRESULT(WINAPI*)(void*,LPCSTR,LPDIACTIONFORMATA,LPDIENUMDEVICESBYSEMANTICSCB,LPVOID,DWORD)>(p->rawVtable[9])(p->raw,user,format,callback,ref,flags);
    if (SUCCEEDED(hr)) ReportUnsupported();
    return hr;
}
static HRESULT WINAPI RootEnumDevicesBySemanticsW(void* self, LPCWSTR user, LPDIACTIONFORMATW format, LPDIENUMDEVICESBYSEMANTICSCB callback, LPVOID ref, DWORD flags) {
    Proxy* p=static_cast<Proxy*>(self);
    HRESULT hr=reinterpret_cast<HRESULT(WINAPI*)(void*,LPCWSTR,LPDIACTIONFORMATW,LPDIENUMDEVICESBYSEMANTICSCB,LPVOID,DWORD)>(p->rawVtable[9])(p->raw,user,format,callback,ref,flags);
    if (SUCCEEDED(hr)) ReportUnsupported();
    return hr;
}
static HRESULT WrapRoot(void** out, bool wide) {
    if (!out || !*out) return E_POINTER;
    GUID zero = {};
    Proxy* p=NewProxy(*out,true,wide,zero,11);
    if (!p) return E_OUTOFMEMORY;
    *out=p;
    return S_OK;
}

extern "C" HRESULT WINAPI DirectInput8Create(HINSTANCE h, DWORD version, REFIID iid, LPVOID* out, LPUNKNOWN outer) {
    InitializeProxy();
    if (!g_realModule) return E_FAIL;
    auto fn=reinterpret_cast<decltype(&DirectInput8Create)>(GetProcAddress(g_realModule,"DirectInput8Create"));
    if (!fn) return E_FAIL;
    HRESULT hr=fn(h,version,iid,out,outer);
    if (SUCCEEDED(hr) && out && *out && !outer && (IsEqualIID(iid,IID_IDirectInput8A)||IsEqualIID(iid,IID_IDirectInput8W))) {
        HRESULT wrapped=WrapRoot(reinterpret_cast<void**>(out),IsEqualIID(iid,IID_IDirectInput8W));
        if (FAILED(wrapped)) { reinterpret_cast<ULONG(WINAPI*)(void*)>((*reinterpret_cast<void***>(*out))[2])(*out); *out=nullptr; return wrapped; }
    }
    return hr;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { InitializeProxy(); auto f=g_realModule?GetProcAddress(g_realModule,"DllCanUnloadNow"):nullptr; return f?reinterpret_cast<HRESULT(WINAPI*)()>(f)():S_FALSE; }
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID c, REFIID i, LPVOID* o) { InitializeProxy(); auto f=g_realModule?GetProcAddress(g_realModule,"DllGetClassObject"):nullptr; return f?reinterpret_cast<HRESULT(WINAPI*)(REFCLSID,REFIID,LPVOID*)>(f)(c,i,o):E_FAIL; }
extern "C" HRESULT WINAPI DllRegisterServer() { InitializeProxy(); auto f=g_realModule?GetProcAddress(g_realModule,"DllRegisterServer"):nullptr; return f?reinterpret_cast<HRESULT(WINAPI*)()>(f)():E_FAIL; }
extern "C" HRESULT WINAPI DllUnregisterServer() { InitializeProxy(); auto f=g_realModule?GetProcAddress(g_realModule,"DllUnregisterServer"):nullptr; return f?reinterpret_cast<HRESULT(WINAPI*)()>(f)():E_FAIL; }
extern "C" LPCDIDATAFORMAT WINAPI GetdfDIJoystick() { InitializeProxy(); auto f=g_realModule?GetProcAddress(g_realModule,"GetdfDIJoystick"):nullptr; return f?reinterpret_cast<LPCDIDATAFORMAT(WINAPI*)()>(f)():nullptr; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
