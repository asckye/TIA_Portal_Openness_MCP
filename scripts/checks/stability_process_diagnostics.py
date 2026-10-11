"""PID-specific Windows diagnostics for the owned offline soak processes."""
import ctypes
from ctypes import wintypes
from collections import Counter
from datetime import datetime, timezone


def snapshot(pid, handle_count):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    result = {}
    process = kernel.OpenProcess(0x1040, False, pid)
    if not process:
        return {'diagnosticError': str(ctypes.WinError(ctypes.get_last_error()))}
    try:
        image = ctypes.create_unicode_buffer(32768); length = wintypes.DWORD(len(image))
        if kernel.QueryFullProcessImageNameW(process, 0, image, ctypes.byref(length)):
            result['image'] = image.value
        ip = ctypes.WinDLL('iphlpapi')
        ip.GetExtendedTcpTable.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL, wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
        sockets = Counter()
        for family, row_size, pid_offset, state_offset in [(2, 24, 20, 0), (23, 56, 52, 48)]:
            size = wintypes.DWORD()
            ip.GetExtendedTcpTable(None, ctypes.byref(size), False, family, 5, 0)
            buffer = ctypes.create_string_buffer(size.value)
            status = ip.GetExtendedTcpTable(buffer, ctypes.byref(size), False, family, 5, 0)
            if status:
                result.setdefault('socketErrors', []).append(status); continue
            count = ctypes.c_uint32.from_buffer(buffer).value
            for index in range(count):
                offset = 4 + index * row_size
                if ctypes.c_uint32.from_buffer(buffer, offset + pid_offset).value == pid:
                    sockets[str(ctypes.c_uint32.from_buffer(buffer, offset + state_offset).value)] += 1
        result['tcpSocketsByState'] = dict(sockets)
        result['tcpSocketCount'] = sum(sockets.values())
        if handle_count >= 2000:
            class Handle(ctypes.Structure):
                _fields_ = [('object', ctypes.c_void_p), ('pid', ctypes.c_size_t), ('value', ctypes.c_size_t),
                            ('access', wintypes.ULONG), ('trace', wintypes.USHORT), ('type', wintypes.USHORT),
                            ('attributes', wintypes.ULONG), ('reserved', wintypes.ULONG)]
            nt = ctypes.WinDLL('ntdll')
            nt.NtQuerySystemInformation.argtypes = [wintypes.ULONG, ctypes.c_void_p, wintypes.ULONG, ctypes.POINTER(wintypes.ULONG)]
            size = 1024 * 1024
            while True:
                buffer = ctypes.create_string_buffer(size); needed = wintypes.ULONG()
                status = nt.NtQuerySystemInformation(64, buffer, size, ctypes.byref(needed))
                if status == 0: break
                if status != -1073741820:
                    raise OSError('NtQuerySystemInformation: ' + hex(status & 0xffffffff))
                size = max(size * 2, needed.value + 65536)
            count = ctypes.c_size_t.from_buffer(buffer).value
            grouped = Counter(); examples = {}; file_candidates = {}
            for index in range(count):
                entry = Handle.from_buffer(buffer, 2 * ctypes.sizeof(ctypes.c_size_t) + index * ctypes.sizeof(Handle))
                if entry.pid == pid:
                    grouped[entry.type] += 1; examples.setdefault(entry.type, entry.value)
                    file_candidates.setdefault(entry.type, []).append(entry.value)
            kernel.GetCurrentProcess.restype = wintypes.HANDLE
            kernel.DuplicateHandle.argtypes = [wintypes.HANDLE, wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.HANDLE), wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            nt.NtQueryObject.argtypes = [wintypes.HANDLE, wintypes.ULONG, ctypes.c_void_p, wintypes.ULONG, ctypes.POINTER(wintypes.ULONG)]
            class Unicode(ctypes.Structure):
                _fields_ = [('length', wintypes.USHORT), ('maximum', wintypes.USHORT), ('buffer', ctypes.c_void_p)]
            types = {}
            file_type = None
            for kind, value in examples.items():
                duplicate = wintypes.HANDLE(); name = 'type-index-' + str(kind)
                if kernel.DuplicateHandle(process, value, kernel.GetCurrentProcess(), ctypes.byref(duplicate), 0, False, 2):
                    try:
                        info = ctypes.create_string_buffer(8192); returned = wintypes.ULONG()
                        if nt.NtQueryObject(duplicate, 2, info, len(info), ctypes.byref(returned)) == 0:
                            text = Unicode.from_buffer(info); name = ctypes.wstring_at(text.buffer, text.length // 2)
                    finally: kernel.CloseHandle(duplicate)
                types[name] = types.get(name, 0) + grouped[kind]
                if name == 'File': file_type = kind
            result['handleSnapshot'] = {'pid': pid, 'atUtc': datetime.now(timezone.utc).isoformat(), 'types': types, 'total': sum(grouped.values())}
            # Query disk paths only: ObjectNameInformation on pipes can block.
            kernel.GetFileType.argtypes = [wintypes.HANDLE]
            kernel.GetFinalPathNameByHandleW.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
            paths = Counter(); candidates = file_candidates.get(file_type, [])
            selected = candidates[:128] + candidates[-128:]
            for value in dict.fromkeys(selected):
                duplicate = wintypes.HANDLE()
                if kernel.DuplicateHandle(process, value, kernel.GetCurrentProcess(), ctypes.byref(duplicate), 0, False, 2):
                    try:
                        if kernel.GetFileType(duplicate) == 1:
                            text = ctypes.create_unicode_buffer(32768)
                            if kernel.GetFinalPathNameByHandleW(duplicate, text, len(text), 0): paths[text.value] += 1
                    finally: kernel.CloseHandle(duplicate)
            result['handleSnapshot']['sampledDiskPaths'] = dict(paths)
            result['handleSnapshot']['selectedFileHandles'] = len(dict.fromkeys(selected))
    except (OSError, ValueError) as error:
        result['diagnosticError'] = str(error)
    finally:
        kernel.CloseHandle(process)
    return result
