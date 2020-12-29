#pragma once

#include <memory_resource>
#include <iostream>
#include <string>
#include <unordered_set>

#include "debug.h"

#if defined(_WIN32) && defined(__USE_CRT_MEM_DEBUG)
// this one does not work well if is not the first object to construct so it won't be the last
// object to de-construct. Some objects deconstruction behind it will get reported. If we can't
// guarantee that, we can use _CrtDumpMemoryLeaks() function directly.
//
struct CrtCheckMemory {
    _CrtMemState state1_;
    _CrtMemState state2_;
    _CrtMemState state3_;
    bool asserit_;

    CrtCheckMemory (bool asserit = true) {
        _CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_LEAK_CHECK_DF);
        _CrtMemCheckpoint (&state1_);
        asserit_ = asserit;
    }

    ~CrtCheckMemory () {
        _CrtMemCheckpoint (&state2_);
        if (_CrtMemDifference (&state3_, &state1_, &state2_)) {
            _CrtDumpMemoryLeaks ();
            _CrtMemDumpStatistics (&state3_);
#if !defined( __DEBUG_CAT_MEMLEAK) && !defined(__DEBUG_PARSER_MEMLEAK)
            // if (asserit_)
            //   assert (false);
#endif
        }
    }
};
#else
struct CrtCheckMemory {
    CrtCheckMemory (bool) {}
    CrtCheckMemory () {}
    ~CrtCheckMemory () {}
};
#endif

// Use this only to debug memory leaks
#define __RUN_DELETES_

#if defined(__USE_VLD_)
#include "common/vld.h"
#endif

using MemoryResource = std::pmr::memory_resource;

// define a new memory resource (not polymorphic allocator)
class DefaultResource : public MemoryResource {
    class AllocStats {
    public:
        uint32_t nallocs_ = 0;
        uint32_t nfrees_ = 0;
    };
    AllocStats stats_;
    std::unordered_set<void*> pointers_;
    std::unordered_multiset<void*> deleted_pointers_;

public:
    MemoryResource* parent_ = nullptr;

    void* do_allocate (std::size_t bytes, std::size_t alignment) override {
        auto r = malloc (bytes);
        pointers_.insert (r);

        stats_.nallocs_++;
        return r;
    }
    void do_deallocate (void* p, std::size_t bytes, std::size_t alignment) override {
        if (deleted_pointers_.find(p) != deleted_pointers_.end())
            abort();
        deleted_pointers_.insert(p);
        pointers_.erase (p);
        std::cerr << "DEBUGMEM: " << __FILE__ << ":" << __LINE__ << " FREE " << p << std::endl;
        free(p);
        stats_.nfrees_++;
    }
    bool do_is_equal (const std::pmr::memory_resource& other) const noexcept override {
        throw NotImplementedException ();
    };

public:
    std::string ToString () {
        std::string str;
        str += "Resource: " + std::to_string ((uintptr_t)this) + "\n";
        str += "\t" + std::to_string (pointers_.size ());
        return str;
    }

    static MemoryResource* CreateMemoryResource (MemoryResource* parent, const char* name) {
        auto resource = new DefaultResource ();
        resource->parent_ = parent;
        return resource;
    }

    static void DeleteMemoryResource (MemoryResource* target) { delete target; }

    void release()
    {
        auto itb = pointers_.begin();
        auto ite = pointers_.end();
        while (itb != ite)
            pointers_.erase(itb++);
        pointers_.clear ();

        auto dtb = deleted_pointers_.begin();
        auto dte = deleted_pointers_.end();
        while (dtb != dte) {
            std::cerr << "DEBUGMEM: " << __FILE__ << ":" << __LINE__ << " DELPTR " << *dtb << std::endl;
            deleted_pointers_.erase(dtb++);
        }
        deleted_pointers_.clear();
    }
    ~DefaultResource () override { release (); }
};

class StackResource : public std::pmr::monotonic_buffer_resource {};

extern thread_local StackResource stackResource_;
extern thread_local DefaultResource defaultResource_;
extern thread_local MemoryResource* currentResource_;

// TODO: add tracing and coroutine-safe
// Here is how memory management works:
// There is a thread safe currentResource_ always available for memory allocation and which memory
// resource it actually pointing to depends on IMemory::SetCurrentResource() call. Initially,
// currentResource_ pointing to DefaultResource which is a bag memory allocator, which can
// allocate/deallocate each memory and dtor release all the rest memory.
//
// There are several common memory usages:
//   - create an object: including make_uique<> etc. If the object inherits IMemory, then it has an
//   overloaded operator new and delete and they are using currentResource_ to allocate memory. You
//   can override this to another MemoryResource by using placement new. Otherwise, you are
//   allocating from global heap space, and you shall free it manually.
//   - std::pmr::*: including string/vector. If any MemoryResource is provided at ctor time, they
//   will allocate from that resource. Usually we use currentResource_ unless you have a fixed
//   resource it shall allocates from.
//   - other object: you may use smart pointers to manage them if the object lifetime is within a
//   function or an object without passing it around, where its memory amount is clear and no leak
//   is possible. However, smart pointers are lack of profiling, so they shall not be used as the
//   major method.
//
class UseCurrentResource {
public:
    // this has to be coroutine-safe
    static MemoryResource* SetCurrentResource (MemoryResource* pmr) {
        auto old = currentResource_;
        currentResource_ = pmr;
        return old;
    }

    void* operator new (size_t size) { return currentResource_->allocate (size); }
    void* operator new (size_t size, MemoryResource* pmr) { return pmr->allocate (size); }
    void* operator new[] (size_t size) { return currentResource_->allocate (size); }
    void operator delete (void* p) { currentResource_->deallocate (p, 0); }
    void operator delete[] (void* p) { currentResource_->deallocate (p, 0); }
};
