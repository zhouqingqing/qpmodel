#pragma once
// things which are slightly different on different platforms
// which include the Compiler, O/S (and it's variations), H/W
// and whatever else. The important property this module have is that
// these differences should be thin wrappers fir for encapsulation, major
// differences don't belong here.

#include <cstring>

namespace andb {
#ifdef _MSC_VER
// compare two C strings in case insensitive mode
inline int andb_strcmp_nocase(const char* str1, const char* str2) { return _stricmp(str1, str2); }
#else

// assume Linux
#include <inttypes.h>
#include <stdint.h>
// should handle more alternatives but for now, let's take it as Unix compatible ENV
inline int andb_strcmp_nocase(const char* str1, const char* str2) { return strcasecmp(str1, str2); }
#endif
}
