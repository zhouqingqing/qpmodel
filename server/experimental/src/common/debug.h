#pragma once

#if !defined(NDEBUG) && defined(_WIN32)
// memory leakage detection
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
    NotImplementedException () : std::logic_error ("not yet implemented.") {}
};

class ParserException : public std::logic_error {
public:
    ParserException () : std::logic_error ("parser error") {}
};

class SemanticException : public std::logic_error {
public:
    SemanticException () : std::logic_error ("semantic error.") {}
};