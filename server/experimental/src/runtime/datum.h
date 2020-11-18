#pragma once

#include <string>
#include <variant>
#include <vector>

#include "common/common.h"
#include "fmtlib/include/fmt/format.h"

namespace andb {

// Datum is a union of all possible typed
//  - of course int, double, strings etc
//  - shall be able to represent null
//  - shall be extensible to accommodates user defined types
//
using NullFlag = std::monostate;
using UserType = void*;

// DataType keep the same order as std::variant<> as they are used as index to access std::varaint.
// Null has to be the first.
//
enum DataType { D_NullFlag = 0, Bool, Int32, Int64, String, Double, D_UserType };
using DatumVariant = std::variant<NullFlag, bool, int32_t, int64_t, std::string, double, UserType>;

class Datum : public DatumVariant {
    using base_type = DatumVariant;

public:  // inherits all ctors
    using base_type::base_type;

public:  // extension methods
    std::string ToString () const {
        const Datum& d = *this;
        std::string s;
        std::visit (
            [&] (auto&& datum) {
                using T = std::decay_t<decltype (datum)>;
                if constexpr (std::is_same_v<T, int>)
                    s = std::to_string (std::get<int> (d));
                else if constexpr (std::is_same_v<T, __int64>)
                    s = std::to_string (std::get<__int64> (d));
                else if constexpr (std::is_same_v<T, std::string>)
                    s = std::get<std::string> (d);
                else if constexpr (std::is_same_v<T, bool>) {
                    bool bv = std::get<bool> (d);
                    s = bv ? " TRUE " : " FALSE ";
                }
                else if constexpr (std::is_same_v<T, NullFlag>)
                    s = "<null>";
            },
            d);

        return s;
    }

    bool IsNull () { return index () == 0; }
    void SetNull () { *this = NullFlag{}; }
};

static_assert (sizeof (NullFlag) == 1);
static_assert (sizeof (Datum) <= 48);

// Row represents a sequence of Datum passing among physical nodes
//    Number of datum in a row is decided at query compiling time, so it is decided at ctor time.
//
class Row {
    using DatumVector = std::vector<Datum>;
    using RowView = std::tuple<Row*, Row*>;
    using Container = std::variant<DatumVector, RowView>;

    std::vector<Datum> values_{};

public:
    explicit Row () = default;
    explicit Row (int n) {
        for (int i = 0; i < n; i++) values_.emplace_back (NullFlag{});
    }
    explicit Row (Row* l, Row* r) {}

    Datum& operator[] (int i) { return values_[i]; }

    bool Equals (const Row* r) {
        return std::equal (values_.begin (), values_.end (), r->values_.begin (),
                           r->values_.end ());
    }

    std::string ToString () {
        std::string s;
        auto size = values_.size ();
        for (int i = 0; i < size - 1; i++) {
            auto a = values_[i];
            s += a.ToString () + ",";
        }
        s += values_[size - 1].ToString ();
        return s;
    }
};
}  // namespace andb
