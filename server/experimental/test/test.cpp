#include <cstdlib>
#include <ctime>
#include <iostream>

#include "gtest/gtest.h"
#include "common/catalog.h"
#include "parser/include/expr.h"
#include "optimizer/binder.h"
#include "parser/include/stmt.h"
#include "parser/include/logicnode.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"

using namespace andb;
using ::testing::EmptyTestEventListener;
using ::testing::InitGoogleTest;
using ::testing::Test;
using ::testing::TestCase;
using ::testing::TestEventListeners;
using ::testing::TestInfo;
using ::testing::TestPartResult;
using ::testing::UnitTest;

constexpr int serializeadd (int n) {
    int r = 0;
    for (int i = 0; i < n; i++) r += i;
    return r;
}

auto MakeQuery (int nrows, Binder *binder) {
    // FIXME: I believe there is no memory leak with this test but if we don't introduce a leak here
    // then CRT gonna capture a list of fake memory leak - don't know why. To better understand it,
    // remove this line you will know.
    //
    // auto intentionally_leak = malloc (1234);
    std::string tabName{ "a" }, col1{ "a1" };
    auto lt = new LogicScan (new BaseTableRef (&tabName), nrows);
    binder->ResolveTable(&tabName);
    BinExpr* filter = new BinExpr(BinOp::Leq, new ColExpr((uint16_t)0, &col1, &tabName), new ConstExpr(22));
    filter->Bind(binder);
    lt->AddFilter (filter);
    auto logic = new LogicAgg (new LogicJoin (lt, new LogicScan (new BaseTableRef (&tabName), nrows * 2)));

    return Optimize (logic);
}

// The fixture for testing class
class DbTest : public Test {
protected:
    DbTest () {}

    virtual ~DbTest () {}

    virtual void SetUp () {}

    virtual void TearDown () {}
};

TEST_F (DbTest, query) {
    CrtCheckMemory checker (false);
    std::srand (std::time (nullptr));
    int nrows = std::rand () % 100 + 1;
    SelectStmt stmt;
    Binder     binder(&stmt, nullptr);
    auto physic = MakeQuery (nrows, &binder);
    int result = 0;
    Execute (physic, [&] (Row* l) {
        result = std::get<int> ((*l)[0]);
        return false;
    });
    EXPECT_EQ (serializeadd (std::min (nrows, 23)), result);
    // Can't release now, or catalog will disappear
    // defaultResource_.release ();
}

TEST_F (DbTest, expr) {
    CrtCheckMemory checker;
    SelectStmt     stmt;
    Binder         binder(&stmt, nullptr);
    auto expr =
        new BinExpr (BinOp::Add, new BinExpr (BinOp::Sub, new ConstExpr (6), new ConstExpr (7)),
                     new BinExpr (BinOp::Mul, new ConstExpr (6), new ConstExpr (7)));
    expr->Bind(&binder);
    // make sure ExprEval dtor happens before we release defaultResource_
    {
        ExprEval eval;
        eval.Open (expr);
        auto result = eval.Exec (nullptr);
        eval.Close ();
        EXPECT_EQ (std::get<Int32> (result), 41);

        Row r (3);
        r[0] = 6;
        r[1] = 7;
        r[2] = 8;
        std::string col1{ "a1" }, col2{ "a2" }, col3{ "a3" }, tabName{ "a" };
        binder.ResolveTable(&tabName);
        expr = new BinExpr (BinOp::Add,
                            new BinExpr (BinOp::Sub, new ColExpr ((uint16_t)0, &col1, &tabName),
                                                     new ColExpr (1, &col2, &tabName)
                                        ),
                            new BinExpr (BinOp::Mul, new ColExpr (2, &col3, &tabName),
                                                     new ConstExpr (3)
                                        )
                            );
        expr->Bind(&binder);
        eval.Open (expr);
        result = eval.Exec (&r);
        eval.Close ();
        EXPECT_EQ (std::get<Int32> (result), 23);

        expr = new BinExpr (BinOp::Equal,
                            new BinExpr (BinOp::Add, new ColExpr ((uint16_t)0, &col1, &tabName), new ColExpr (2, &col3, &tabName)),
                            new BinExpr (BinOp::Add, new ColExpr (1, &col2, &tabName), new ColExpr (1, &col2, &tabName)));
        expr->Bind(&binder);
        eval.Open (expr);
        result = eval.Exec (&r);
        eval.Close ();
        EXPECT_EQ (std::get<Bool> (result), true);
    }

    // done
    // Can't release now, or catalog will disappear
    // defaultResource_.release ();
}

// setup memory break handler as early as possible
//
#ifndef NDEBUG
static int breakAlloc = (_crtBreakAlloc = -1);
#endif
int main (int argc, char** argv) {
    InitGoogleTest (&argc, argv);
    Catalog::Init();
    int ret = RUN_ALL_TESTS ();
    defaultResource_.release();
    return ret;
}
