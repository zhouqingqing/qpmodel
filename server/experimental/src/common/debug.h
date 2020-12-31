#pragma once

/*
 * Aid to debug catalog memory leaks. There are still two blocks
 * not freed. This code may be left in and __DEBUG_CAT_MEMLEAK
 * turned into a runtime debug level options.
 */
// #define __DEBUG_CAT_MEMLEAK
// #define __DEBUG_PARSER_MEMLEAK
// #define __ENABLE_DEBUG_MSG
// #define __DEBUG_MEMORY

#if !defined(NDEBUG) && defined(_WIN32)
#define __USE_CRT_MEM_DEBUG
//#define __USE_VLD_
#endif

#if !defined(NDEBUG) && defined(_WIN32) && defined(__USE_CRT_MEM_DEBUG)
#define _CRTDBG_MAP_ALLOC
#include <crtdbg.h>
#include <stdlib.h>
#endif

#include <cassert>
#include <stdexcept>

#ifndef NDEBUG
#define DBG_ONLY(x) x
#else
#define DBG_ONLY(x)
#endif

class NotImplementedException : public std::logic_error
{
    public:
    NotImplementedException(const char* extra = nullptr)
      : std::logic_error(extra ? extra : "not yet implemented.")
    {}
};
