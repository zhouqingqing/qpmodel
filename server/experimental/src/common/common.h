#pragma once

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

   inline bool IsQuotedName(const std::string &name)
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
         RandomDevice(const RandomDevice &) = delete;
         RandomDevice& operator=(const RandomDevice&) = delete;

      private:
         std::random_device               rdev;
         std::mt19937                     rgen;
         std::uniform_int_distribution<> rdist;
   };
}
