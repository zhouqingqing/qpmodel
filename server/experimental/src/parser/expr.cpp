#include "parser/include/expr.h"
#include "parser/include/stmt.h"

#include <unordered_map>
namespace andb {
#pragma region binfunctions
// we want to keep smaller function separately than in a template because we can run clang to get
// their LLVM/IR code without have to substantiate them. The other reason is that huge template
// function is not friendly to debugger.
//
Datum fn_AddInt32Int32 (Datum* l, Datum* r) {
    int result = std::get<Int32> (*l) + std::get<Int32> (*r);
    return Datum{result};
}

Datum fn_MulInt32Int32 (Datum* l, Datum* r) {
    int result = std::get<Int32> (*l) * std::get<Int32> (*r);
    return Datum{result};
}

Datum fn_SubInt32Int32 (Datum* l, Datum* r) {
    int result = std::get<Int32> (*l) - std::get<Int32> (*r);
    return Datum{result};
}

Datum fn_EqualInt32Int32 (Datum* l, Datum* r) {
    bool result = std::get<Int32> (*l) == std::get<Int32> (*r);
    return Datum{result};
}

Datum fn_LeqInt32Int32 (Datum* l, Datum* r) {
    bool result = std::get<Int32> (*l) <= std::get<Int32> (*r);
    return Datum{result};
}
#pragma endregion binfunctions

struct BinFunctionDesc {
    BinOp op_;
    DataType ltype_;
    DataType rtype_;

    // operator == and hash is needed by unordered_map<>
    //
    bool operator== (const BinFunctionDesc& other) const {
        return op_ == other.op_ && ltype_ == other.ltype_ && rtype_ == other.rtype_;
    }

    struct HashFn {
        std::size_t operator() (const BinFunctionDesc& k) const {
            using std::hash;
            return hash<int> () (k.op_) ^ hash<int> () (k.ltype_) ^ hash<int> () (k.rtype_);
        }
    };
};

struct BinFunctionDetails {
    DataType rettype_;
    BinExpr::BinFunction fn_;
};

static std::unordered_map<BinFunctionDesc, BinFunctionDetails, BinFunctionDesc::HashFn> BinMap_ = {
    {{Add, DataType::Int32, DataType::Int32}, {DataType::Int32, fn_AddInt32Int32}},
    {{Sub, DataType::Int32, DataType::Int32}, {DataType::Int32, fn_SubInt32Int32}},
    {{Mul, DataType::Int32, DataType::Int32}, {DataType::Int32, fn_MulInt32Int32}},
    {{Equal, DataType::Int32, DataType::Int32}, {DataType::Bool, fn_EqualInt32Int32}},
    {{Leq, DataType::Int32, DataType::Int32}, {DataType::Bool, fn_LeqInt32Int32}},
};

void BinExpr::bindFunction () {
    auto key = BinFunctionDesc{op_, children_[0]->type_, children_[1]->type_};
    auto search = BinMap_.find (key);
    if (search == BinMap_.end ()) {
        throw SemanticException ();
    }
    fn_ = search->second.fn_;
    type_ = search->second.rettype_;
}

Expr* makeStar (std::string* alias) {
    Expr* e = new SelStar (alias);

    return e;
}

Expr* makeOpBinary (Expr* left, BinOp op, Expr* right) {
    Expr* e = new BinExpr (op, left, right);

    return e;
}

Expr* makeNullLiteral () {
    Expr* e = new ConstExpr ("null");

    return e;
}

Expr* makeLiteral (const char* cval) {
    Expr* e = new ConstExpr (Datum (std::string (cval)));

    return e;
}

Expr* makeLiteral (std::string* sval) {
    Expr* e = new ConstExpr (Datum (sval));

    return e;
}

Expr* makeLiteral (double dval) {
    Expr* e = new ConstExpr (Datum (dval));

    return e;
}

Expr* makeLiteral (int64_t ival) {
    Expr* e = new ConstExpr (Datum (ival));

    return e;
}

Expr* makeLiteral (bool bval) {
    Expr* e = new ConstExpr (Datum (bval));
    return e;
}

Expr* makeColumnRef (char* cname, char* alias) {
    Expr* e = new ColExpr (cname);

    return e;
}

Expr* makeColumnRef (std::string* cname, std::string* alias) {
    Expr* e = new ColExpr (const_cast<char*> (cname->c_str ()));

    return e;
}

char* substr (const char* source, int from, int to) {
    int len = to - from;
    char* copy = new char[len + 1];
    strncpy (copy, source + from, len);
    copy[len] = '\0';
    return copy;
}



// given an expression, enqueue all ops
void ExprEval::Open (Expr* expr) {
    if (expr == nullptr) return;
    expr_ = expr;

    // TBD: move this to planning stage
    BindContext context;
    expr->Bind (context);

    // here is how evaluation works
    //   - enque decides the FIFO order which expr get evaluated first. This follows post-order
    //   per correctness. This order also decides how many slots we need for evaluation. Look
    //   the example below, if we switch the left right tree, the slot tree can becomes
    //   0(0(0)(1))(1) which means we only need 2 slots instead of 3.
    //   - slot decides where the expr dump the results. Basic rule is if current node is
    //   parent's nth child, it will use slot parent + nth. The expression thus can reuse slot
    //   for temp results dumping without dynamic memory allocation for every expression.
    //
    //    0                    4
    //   /  \                 / \
    //   0   1               0   3
    //       /\                 / \  
    //      1  2               1   2
    //  [slot]               [enque]
    //

    // 1. first pre-order traversal assigns slot
    uint32_t maxslot = 0;
    expr->deepVisitParentChild ([&] (Expr* parent, int level, int nth, Expr* e) {
        uint32_t slot = nth == -1 ? 0 : parent->slot_ + nth;
        e->slot_ = slot;
        maxslot = std::max (slot, maxslot);
    });
    maxslot += 1;
    board_ = new Datum[maxslot];
    pointer_ = new Datum*[maxslot];
    for (int i = 0; i < maxslot; i++) pointer_[i] = board_ + i;

    // 2. second post-order traversal enqueue the expr
    expr->deepVisit<TraOrder::PostOrder> ([&] (Expr* e) { queue_.push_back (e); });
}

Datum ExprEval::Exec (Row* l) {
    // TBD: can we use std::move() instead of doing pointer/board?
    Datum* board = board_;
    Datum** pointer = pointer_;

    // compute the result
    //  1. do not write to memory unnecessary (so no need to copy ColExpr, ConstExpr etc)
    //  2. do not write more than you not (so do std::get<>)
    //  3. do not waste instructions on glue code (so use computed goto, avoid fn call)
    //
    for (auto a : queue_) {
        auto curslot = a->slot_;
        switch (a->classTag_) {
            case BinExpr_: {
                auto e = static_cast<BinExpr*> (a);
                auto l = pointer[e->children_[0]->slot_];
                auto r = pointer[e->children_[1]->slot_];
                board[curslot] = e->fn_ (l, r);
                pointer[curslot] = &board[curslot];
            } break;
            case ConstExpr_: {
                // only store the pointer
                auto e = static_cast<ConstExpr*> (a);
                pointer[curslot] = &e->value_;
            } break;
            case ColExpr_: {
                // only store the pointer
                auto e = static_cast<ColExpr*> (a);
                pointer[curslot] = &(*l)[e->ordinal_];
            } break;
            default:
                assert (false);
        }
    }

    return board[0];
}

void ExprEval::Close () {
    if (expr_ != nullptr) {
        delete[] board_;
        delete[] pointer_;
        queue_.clear ();
    }
}

}  // namespace andb
