#pragma once

#include <iostream>
#include <exception>
#include <cctype>
#include <string>
#include <algorithm>
#include <limits>
#include <numeric>
#include <random>

#include "common/platform.h"
#include "debug.h"
#include "memory.h"

namespace andb {
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
}
