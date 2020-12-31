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


#include "common/platform.h"
#include "debug.h"
#include "memory.h"

// debug memory mamagement
#ifdef __DEBUG_MEMORY

class SysTable;
class SysStats;
class TableDef;
class TableRef;

static const char *nillName = "NULL";

#ifdef _MSC_VER
#define PASS_ON_VARARGS(...)    __VAR_ARGS__
#else
#define PASS_ON_VARARGS
#endif
SysTable *newSysTable(const char *file, int line)
{
    SysTable* tab = new SysTable();
    std::cerr << file << " " << line << " SysTable "
        << (void *)tab << " size " << sizeof(SysTable) << std::endl;

    return tab;
}
#define NEW_SYSTABLE_TYPE() newSysTable(__FILE__, __LINE__)

SysStats *newSysStats(const char *file, int line)
{
    SysStats* tab = new SysStats();
    std::cerr << file << " " << line << << " SysStats "
        << (void *)tab << " size " << sizeof(SysStats) << std::endl;

    return tab;
}
#define NEW_SYSSTATS_TYPE() newSysStats(__FILE__, __LINE__)

TableDef *newTableDef(const char *file, int line, ...)
{
    va_list args;
    va_start(args, line);
    TableDef *ptr = new TableDef(args);
    std::cerr << file << " " << line << " TableDef "
        << (void *)ptr << " name " << ptr->name_ << " size "
        << sizeof(TableDef) << std::endl;

    rteturn ptr;
}
#define NEW_TABLEDEF_TYPE() newTableDef(__FILE__, __LINE__, ...)

TableRef *newTableRef(const char *file, int line, ...)
{
    va_list args;
    va_start(args, line);
    TableRef *ptr = new TableRef(args);
    std::cerr << file << " " << line << " TableRef "
        << (void *)ptr << " alias " << (ptr->alias_ ? ptr->alias_->c_str() :
                nillName) << " size " << sizeof(TableRef) << std::endl;

    rteturn ptr;
}
#define NEW_TABLEREF_TYPE() newTableRef(__FILE__, __LINE__, ...)

#else
#define NEW_SYSTABLE_TYPE() SysTable* Catalog::systable_ = new SysTable()
#define NEW_SYSSTATS_TYPE() SysStats* Catalog::sysstat_ = new SysStats()
#endif // __DEBUG_MEMORY

#ifdef __DEBUG_CAT_MEMLEAK
    #define DBG_NEW new ( _NORMAL_BLOCK , __FILE__ , __LINE__ )
    // Replace _NORMAL_BLOCK with _CLIENT_BLOCK if you want the
    // allocations to be of _CLIENT_BLOCK type
#else
    #define DBG_NEW new
#endif


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
      AutoDebug(const char *file, int line,
            int num = 0, const char *head = nullptr,
            const char *body = nullptr)
      {
         std::cout << "ENTRY { : " << file << " : " << line
                  << " : num " << num
                  << " : head " << (head ? head : "")
                  << " : body " << (body ? body : "") << ":" << std::endl;
         file_ = file;
         num_ = num;
      }

      ~AutoDebug()
      {
         std::cout << "EXIT  : " << file_ << " : "
               << " : num " << num_ << ":}" << std::endl;
      }

      private:
         const char *file_;
         int        num_;
   };

#define ADBG(...) AutoDebug adbg_##__LINE__{__FILE__, __LINE__, __VA_ARGS__}

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
