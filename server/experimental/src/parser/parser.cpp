#include "common/common.h"
#include "common/dbcommon.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "parser/include/SQLParserResult.h"
#include "parser/include/parser_typedef.h"

#include "parser/syntax/andb_parser.h"
#include "parser/syntax/andb_lexer.h"

#include "parser/include/parser.h"

namespace andb {

// semantic analyze
//
LogicNode* Analyze (SQLStatement* stmt) {
    auto root = new LogicAgg (new LogicJoin (new LogicScan (new BaseTableRef ("a"), 3),
                                             new LogicScan (new BaseTableRef ("b"), 4)));
    return root;
}

// select a1, a2 from a where a2>=1;
SelectStmt* make_q1 () {
    auto stmt = new SelectStmt ();
    stmt->from_ = {new BaseTableRef ("a")};
    stmt->selection_ = {new ColExpr ("a1"), new ColExpr ("a2")};
    stmt->where_ = new BinExpr (BinOp::Leq, new ConstExpr (1), new ColExpr ("a2"));
    return stmt;
}

// select a1, b1 from a join b on a.a1=b.b1
SelectStmt* make_q2 () {
    auto stmt = new SelectStmt ();
    stmt->from_ = {new BaseTableRef ("a"), new BaseTableRef ("b")};
    stmt->selection_ = {new ColExpr ("a1"), new ColExpr ("b1")};
    stmt->where_ = new BinExpr (BinOp::Equal, new ColExpr ("a1"), new ColExpr ("b1"));
    return stmt;
}
// Parse a query statement
//
SQLStatement* Parse (char* query) {
    // TBD: call bison raw parser here
    auto stmt = new SelectStmt ();
    stmt = make_q1 ();
    return stmt;
}

// Parse an expression string
//
Expr* ParseExpr (char* expr) { return nullptr; }

// Given a query, parse and semantic analyze, output its logic plan
//
LogicNode* ParseAndAnalyze (char* query) {
    auto stmt = Parse (query);
    auto logic = Analyze (stmt);
    return logic;
}

bool ParseSQL (char* query, SQLParserResult* result) {
   yyscan_t scanner;
   YY_BUFFER_STATE state;

    if (andb_lex_init (&scanner)) {
        throw ParserException();
    }

    state = andb__scan_string (query, scanner);
    int ret = andb_parse (result, scanner);

    result->setIsValid (ret == 0);

    andb__delete_buffer (state, scanner);
    andb_lex_destroy (scanner);

    return ret == 0;
}
}  // namespace andb
