#include <windows.h>
#include <tlhelp32.h>
#include <string>
#include <iostream>
#include <cstdint>  

static DWORD get_process_id(const wchar_t* process_name)
{
    DWORD process_id = 0;

    // Take a snapshot of all processes
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
        return 0;

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);

    if (Process32FirstW(snapshot, &entry))
    {
        do
        {
            if (_wcsicmp(process_name, entry.szExeFile) == 0)
            {
                process_id = entry.th32ProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return process_id;
}

static std::uintptr_t get_module_base(const DWORD pid, const wchar_t* module_name)
{
    std::uintptr_t module_base = 0;

    // Take a snapshot of the process's modules (DLLs)
    HANDLE snapshot = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid
    );
    if (snapshot == INVALID_HANDLE_VALUE)
        return 0;

    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);

    if (Module32FirstW(snapshot, &entry))
    {
        do
        {
            if (wcsstr(entry.szModule, module_name) != nullptr)
            {
                module_base = reinterpret_cast<std::uintptr_t>(entry.modBaseAddr);
                break;
            }
        } while (Module32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return module_base;
}

namespace driver
{
    namespace codes
    {
        constexpr ULONG attach = CTL_CODE(FILE_DEVICE_UNKNOWN,
            0x696,
            METHOD_BUFFERED,
            FILE_SPECIAL_ACCESS);

        constexpr ULONG read = CTL_CODE(FILE_DEVICE_UNKNOWN,
            0x697,
            METHOD_BUFFERED,
            FILE_SPECIAL_ACCESS);

        constexpr ULONG write = CTL_CODE(FILE_DEVICE_UNKNOWN,
            0x698,
            METHOD_BUFFERED,
            FILE_SPECIAL_ACCESS);
    }

    struct Request
    {
        HANDLE  process_id;
        PVOID   target;
        PVOID   buffer;
        SIZE_T  size;
        SIZE_T  return_size;
    };

    bool attach_to_process(HANDLE drv, DWORD pid)
    {
        Request r{};
        r.process_id = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(pid));   // <-- missing!
        DWORD dummy;
        return DeviceIoControl(drv, codes::attach, &r, sizeof(r), &r, sizeof(r), &dummy, nullptr);
    }

    template<typename T>
    T read_memory(HANDLE drv, uintptr_t addr, DWORD pid)
    {
        T out{};
        Request r{};
        r.process_id = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(pid));   // <-- missing!
        r.target = reinterpret_cast<void*>(addr);
        r.buffer = &out;
        r.size = sizeof(T);
        DeviceIoControl(drv, codes::read, &r, sizeof(r), &r, sizeof(r), nullptr, nullptr);
        return out;
    }
    template <class T>
    void write_memory(HANDLE driver_handle, const std::uintptr_t addr, const T& value) {
        Request r;
        r.target = reinterpret_cast<PVOID>(addr);
        r.buffer = (PVOID)&value;
        r.size = sizeof(T);

        DeviceIoControl(
            driver_handle,
            codes::write,
            &r,
            sizeof(r),
            &r,
            sizeof(r),
            nullptr,
            nullptr
        );
    }

}



int main() {
    const DWORD pid = get_process_id(L"notepad.exe");

    if (pid == 0) {
        std::cout << "Failed to find notepad\n";
        std::cin.get();
        return 1;
    }
    const HANDLE driver = CreateFile(
        L"\\\\.\\SexyDriver",
        GENERIC_READ,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );

    if (driver == INVALID_HANDLE_VALUE) {
        std::cout << "Failed to create our driver handle.\n";
        std::cin.get();
        return 1;
    }

    if (driver::attach_to_process(driver, pid) == true) {
        std::cout << "Attachment successful.\n";
    }

    CloseHandle(driver);
    std::cin.get();

    return 0;
}
