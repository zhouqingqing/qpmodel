#pragma once

#include <deque>
#include <string>

#include "common/dbcommon.h"
#include "common/nodebase.h"
#include "runtime/datum.h"

namespace andb {
class BindContext {};

class Expr : public RuntimeNodeT<Expr> {
protected:
    using base_type = Expr;

public:
    ClassTag classTag_;
    DataType type_;

    // evaluation support
    uint32_t slot_;

    virtual std::string Explain (void* arg = nullptr) const { return {}; }
    virtual void Bind (BindContext& context) {
        auto nchildren = childrenCount ();
        for (int i = 0; i < nchildren; i++) child (i)->Bind (context);
    };
};

class ConstExpr : public NodeBase<Expr, N0> {
public:
    Datum value_;

    explicit ConstExpr (Datum value) {
        assert (value_ == Datum{});
        classTag_ = ConstExpr_;
        value_ = value;
        type_ = (DataType)value.index ();
    }
};

class ColExpr : public NodeBase<Expr, N0> {
public:
    uint16_t ordinal_;
    char* colname_;

    explicit ColExpr (uint16_t ordinal) {
        classTag_ = ColExpr_;
        ordinal_ = ordinal;
        colname_ = nullptr;
    };
    explicit ColExpr (char *colname) {
        classTag_ = ColExpr_;
        ordinal_ = UINT16_MAX;
        colname_ = colname;
    };
    void Bind (BindContext& context) { type_ = Int32; }
};

enum BinOp : uint16_t { Add = 0, Sub, Mul, Equal, Leq };

class BinExpr : public NodeBase<Expr, N2> {
public:
    using BinFunction = Datum (*) (Datum* l, Datum* r);

    BinOp op_;
    BinFunction fn_;

    explicit BinExpr (BinOp op, Expr* l, Expr* r) {
        classTag_ = BinExpr_;
        op_ = op;
        children_[0] = l;
        children_[1] = r;
    }

    void Bind (BindContext& context) {
        Expr::Bind (context);
        bindFunction ();
    }

    void bindFunction ();
};

class ExprEval {
    Expr* expr_ = nullptr;
    Datum** pointer_ = nullptr;
    Datum* board_ = nullptr;

public:
    std::pmr::deque<Expr*> queue_{currentResource_};

public:
    // given an expression, enqueue all ops
    void Open (Expr* expr);
    Datum Exec (Row* l);
    void Close ();
};
}  // namespace andb
