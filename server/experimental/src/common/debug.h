#pragma once
// #define __USE_CRT_MEM_DEBUG
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

class NotImplementedException : public std::logic_error {
public:
    NotImplementedException (const char* extra = nullptr)
        : std::logic_error (extra ? extra : "not yet implemented.") {}
};
