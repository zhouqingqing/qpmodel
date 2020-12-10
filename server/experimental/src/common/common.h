#pragma once

#include <algorithm>
#include <cctype>
#include <exception>
#include <functional>
#include <iostream>
#include <limits>
#include <numeric>
#include <random>
#include <string>
#include <cstdio>    // __VAR_ARGS__

// #define __RUN_DELETES_
/*
* Aid to debug catalog memory leaks. There are still two blocks
* not freed. This code may be left in and __DEBUG_CAT_MEMLEAK
* turned into a runtime debug level options.
*/
// #define __DEBUG_CAT_MEMLEAK
// #define __DEBUG_PARSER_MEMLEAK
// #define __ENABLE_DEBUG_MSG

// debug memory mamagement
#ifdef __DEBUG_MEMORY
#define NEW_SYSTABLE_TYPE()  do {\
SysTable* Catalog::systable_ = new SysTable(); \
      std::cerr << __FILE__ << ": " << __LINE__ << " : " << __func__ \
               << ": SysTable : " << (void *)Catalog::systable_ << " : " \
               << sizeof(SysTable) << std::endl;
} while (false)

#define NEW_SYSSTATS_TYPE()  do {\
SysStats* Catalog::sysstats_ = new SysStats(); \
      std::cerr << __FILE__ << ": " << __LINE__ << " : " << __func__ \
               << ": SysStats : " << (void *)Catalog::sysstats_ << " : " \
               << sizeof(SysStats) << std::endl;
} while (false)

#define NEW_TABLEDEF_TYPE(ptr__, __VAR_ARGS__) do { \
TableDef* ptr__ = new TableDef(__VAR_ARGS__); \
      std::cerr << __FILE__ << ": " << __LINE__ << " : " << __func__ \
               << ": TableDef : " << (void *)ptr__ << " : " \
               << sizeof(TableDef) << std::endl;
#else
#define NEW_SYSTABLE_TYPE() SysTable* Catalog::systable_ = new SysTable()
#define NEW_SYSSTATS_TYPE() SysStats* Catalog::sysstat_  = new SysStats()
#define NEW_TABLEDEF_TYPE(ptr__, __VAR_ARGS__) TableDef* ptr__ = new TableDef(__VAR_ARGS__)
#endif // __DEBUG_MEMORY

#ifdef __DEBUG_CAT_MEMLEAK
    #define DBG_NEW new ( _NORMAL_BLOCK , __FILE__ , __LINE__ )
    // Replace _NORMAL_BLOCK with _CLIENT_BLOCK if you want the
    // allocations to be of _CLIENT_BLOCK type
#else
    #define DBG_NEW new
#endif

#include "common/platform.h"
#include "debug.h"
#include "memory.h"

namespace andb {
#ifdef __ENABLE_DEBUG_MSG
   // Simple interface to debug messages and to track memory usage
   // This is compile time construct.
   // We can add some interesting local variables to be prined too
   // with a vector of ANY *, but ... not now.
   //
   class AutoDebug
   {
      public:
      AutoDebug(const char *file, int line, const char *func,
            int num = 0, const char *head = nullptr,
            const char *body = nullptr)
      {
         std::cout << "ENTRY { : " << file << " : " << line
                  << " : " << func << " : num " << num
                  << " : head " << (head ? head : "")
                  << " : body " << (body ? body : "") << ":" << std::endl;
         file_ = file;
         func_ = func;
         num_ = num;
      }

      ~AutoDebug()
      {
         std::cout << "EXIT  : " << file_ << " : " << func_
               << " : num " << num_ << ":}" << std::endl;
      }

      private:
         const char *file_;
         const char *func_;
         int        num_;
   };

#define ADBG(...) AutoDebug adbg_##__LINE__{__FILE__, __LINE__, __func__, __VA_ARGS__}

#define DEBUG_CONS(class___, extra___) \
    std::cout << __FILE__ << ": " \
   << __LINE__ << ": " << class___ "_cons(" << extra___ << ") : PTR " \
   << (void *)this << std::endl;

#define DEBUG_DEST(class___, extra___) \
    std::cout << __FILE__ << ": " \
   << __LINE__ << ": " << class___ "_dest(" << extra___ << ") : PTR " \
   << (void *)this << std::endl;
#else
#define ADBG(...)
#define DEBUG_CONS(...)
#define DEBUG_DEST(...)
#endif // __ENABLE_DEBUG_MSG

    class QueryOptions
    {
        // Just a place holder for now.
        public:
        QueryOptions(){}
        ~QueryOptions(){}
    };

   // modifies input string
   inline std::string *StdStrLower(std::string* inStr)
   {
    std::transform (inStr->begin (), inStr->end (), inStr->begin (),
                    [] (unsigned char c) { return std::tolower (c); });
      return inStr;
   }

   inline bool IsQuotedName(std::string &name)
   {
      return name[0] == '"';
   }

   // for now, there are no quoted names.
   inline bool RemoveQuotes(std::string &name)
   {
      return false;
   }

class CaselessStringPtrCmp {
public:
    bool operator() (const std::string* lv, const std::string* rv) const {
        return lv->compare (*rv) < 0;
    }
};

struct CaselessStringPtrHash {
public:
    decltype (auto) operator() (const std::string* p) const {
        std::string* ptr = const_cast<std::string*> (p);
        return std::hash<std::string> () (*ptr);
    }
};

   class RandomDevice {
      public:
         int Next()
         {
            return abs(static_cast<int> (rdist (rgen)));
         }

         RandomDevice () : rgen (rdev ()), rdist (1, std::numeric_limits<int>::max ()) {}
         RandomDevice(RandomDevice &) = delete;
         RandomDevice& operator=(RandomDevice&) = delete;

      private:
         std::random_device               rdev;
         std::mt19937                     rgen;
         std::uniform_int_distribution<> rdist;
   };

   class SemanticAnalyzeException : public std::exception {
        public:
            SemanticAnalyzeException (const std::string& msg)
            {
                std::cerr << "ANDB ERROR[SAE]: " << msg << std::endl;
            }
   };

   class SemanticExecutionException : public std::runtime_error
   {
        public:
            SemanticExecutionException (const std::string& msg) : runtime_error (msg)
            {
                std::cerr << "ANDB ERROR[SEE]: " << msg << std::endl;
            }

            SemanticExecutionException (const char* msg) : runtime_error (msg) {
                std::cerr << "ANDB ERROR[SEE]: " << msg << std::endl;
            }
   };

   class SemanticException : public std::logic_error
   {
        public:
            SemanticException (const std::string& msg) : logic_error (msg)
            {
                std::cerr << "ANDB ERROR[SME]: " << msg << std::endl;
            }

            SemanticException (const char* msg) : logic_error (msg) {
                std::cerr << "ANDB ERROR:[SME]: " << msg << std::endl;
            }
   };

   class RuntimeException : public std::runtime_error
   {
       public:
       RuntimeException(const std::string& msg)
         : runtime_error(msg)
       {
           std::cerr << "ANDB ERROR[RTE]" << msg << std::endl;
       }
   };

   class ParserException : public std::logic_error {
   public:
       ParserException (const char *msg = 0)
          : std::logic_error (msg ? msg : "ANDB ERROR[PAE]")
       {
       }

       ParserException (const std::string& msg)
          : std::logic_error (msg.c_str())
       {
       }
   };
} // namespace andb
