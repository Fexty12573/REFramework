#pragma once

#include "StackWalker.h"

#include <spdlog/spdlog.h>
#include <fstream>


class StackDumper : public StackWalker
{
public:
    StackDumper() : StackWalker() { }
protected:
    void OnOutput(LPCSTR szText) override {
        spdlog::error(szText);
    }

    void OnLoadModule(
		LPCSTR img,
		LPCSTR mod,
		DWORD64 baseAddr,
		DWORD size,
		DWORD result,
		LPCSTR symType,
		LPCSTR pdbName,
		ULONGLONG fileVersion) override {
	    // StackWalker::OnLoadModule(img, mod, baseAddr, size, result, symType, pdbName, fileVersion);
	}

    void OnDbgHelpErr(LPCSTR szFuncName, DWORD gle, DWORD64 addr) override {
	    // StackWalker::OnDbgHelpErr(szFORCE_KEY_PROTECTION, gle, addr);
	}

    void OnCallstackEntry(CallstackEntryType eType, CallstackEntry& entry) override {
		CHAR   buffer[STACKWALK_MAX_NAMELEN];
		size_t maxLen = STACKWALK_MAX_NAMELEN;
#if _MSC_VER >= 1400
		maxLen = _TRUNCATE;
#endif
		if ((eType != lastEntry) && (entry.offset != 0))
		{
			if (entry.name[0] == 0)
				strncpy_s(entry.name, STACKWALK_MAX_NAMELEN, "(function-name not available)", _TRUNCATE);
			if (entry.undName[0] != 0)
				strncpy_s(entry.name, STACKWALK_MAX_NAMELEN, entry.undName, _TRUNCATE);
			if (entry.undFullName[0] != 0)
				strncpy_s(entry.name, STACKWALK_MAX_NAMELEN, entry.undFullName, _TRUNCATE);
			if (entry.lineFileName[0] == 0)
			{
				strncpy_s(entry.lineFileName, STACKWALK_MAX_NAMELEN, "(filename not available)", _TRUNCATE);
				if (entry.moduleName[0] == 0)
					strncpy_s(entry.moduleName, STACKWALK_MAX_NAMELEN, "(module-name not available)", _TRUNCATE);
				_snprintf_s(buffer, maxLen, "%p (%s): %s: %s", (LPVOID)entry.offset, entry.moduleName, entry.lineFileName, entry.name);
			}
			else
				_snprintf_s(buffer, maxLen, "%s (%d): %s (%p)", entry.lineFileName, entry.lineNumber, entry.name, (LPVOID)entry.offset);
			buffer[STACKWALK_MAX_NAMELEN - 1] = 0;
			OnOutput(buffer);
		}
	}
};
