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

/*
* Aid to debug catalog memory leaks. There are still two blocks
* not freed. This code may be left in and DEBUG_CAT_MEMLEAK
* turned into a runtime debug level options.
*/
// #define DEBUG_CAT_MEMLEAK

#ifdef DEBUG_CAT_MEMLEAK
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
                std::cerr << "ANDB ERROR[SAE]: " << msg << "\n";
            }
   };

   class SemanticExecutionException : public std::runtime_error
   {
        public:
            SemanticExecutionException (const std::string& msg) : runtime_error (msg)
            {
                std::cerr << "ANDB ERROR[SEE]: " << msg << "\n"; 
            }

            SemanticExecutionException (const char* msg) : runtime_error (msg) {
                std::cerr << "ANDB ERROR[SEE]: " << msg << "\n";
            }
   };

   class SemanticException : public std::logic_error
   {
        public:
            SemanticException (const std::string& msg) : logic_error (msg)
            {
                std::cerr << "ANDB ERROR[SME]: " << msg << "\n";
            }

            SemanticException (const char* msg) : logic_error (msg) {
                std::cerr << "ANDB ERROR:[SME]: " << msg << "\n";
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
