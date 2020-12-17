#include <string>
#include <vector>
#include <variant>

#include "common/common.h"
#include "fmtlib/include/fmt/format.h"
#include "runtime/datum.h"

namespace andb {
template<class... Ts> struct overload : Ts... {using Ts::operator()...;};
template<class... Ts> overload(Ts...) -> overload<Ts...>;

std::string Datum::ToString () const {
        const Datum& d = *this;
        std::string s;
#ifdef _MSC_VER
        std::visit (
            [&] (auto&& arg) {
                using T = std::decay_t<decltype (arg)>;
                if constexpr (std::is_same_v<T, int>)
                    s = std::to_string (std::get<int> (d));
                else if constexpr (std::is_same_v<T, int64_t>)
                    s = std::to_string (std::get<int64_t> (d));
                else if constexpr (std::is_same_v<T, std::string>)
                    s = std::get<std::string> (d);
                else if constexpr (std::is_same_v<T, bool>) {
                    bool bv = std::get<bool> (d);
                    s = bv ? "true" : "false";
                }
                else if constexpr (std::is_same_v<T, NullFlag>)
                    s = "<null>";
            },
            d);
#else
        // Assume Linux:
        // clang 11, and gcc 10.2 can't compile the msvc version above
        // so this is an alternative until we find something which
        // is as elegant as the accepted by msvc. 
        if (const auto intPtr(std::get_if<int32_t>(&d)); intPtr)
            s = std::to_string(*intPtr);
        else if (const auto longPtr(std::get_if<int64_t>(&d)); longPtr)
            s = std::to_string(*longPtr);
        else if (const auto strPtr(std::get_if<std::string>(&d)); strPtr)
            s = *strPtr;
        else if (const auto boolPtr(std::get_if<bool>(&d)); boolPtr) {
            s = *boolPtr ? "true" : "false";
        }
        else if (const auto nullPtr(std::get_if<NullFlag>(&d)); nullPtr)
            s = "<null>";
#endif
        return s;
    }
}
