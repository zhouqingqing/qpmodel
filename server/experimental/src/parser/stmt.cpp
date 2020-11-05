#include "include/logicnode.h"
#include "include/stmt.h"

using namespace andb;

// from clause -
//  pair each from item with cross join, their join conditions will be handled
//  with where clause processing.
//
LogicNode* SelectStmt::transformFromClause () {
    auto transformOneFrom = [] (TableRef* tab) {
        LogicNode* from = nullptr;
        switch (tab->classTag_) {
            case BaseTableRef_: {
                auto bref = static_cast<BaseTableRef*> (tab);
                from = new LogicScan (bref, 3);
            } break;
            default:
                break;
        }

        return from;
    };

    LogicNode* root;
    if (from_.size () >= 2) {
        auto join = new LogicJoin (nullptr, nullptr);
        auto children = join->children_;

        for (auto x : from_) {
            auto from = transformOneFrom (x);
            if (children[0] == nullptr)
                children[0] = from;
            else
                children[1] = (children[1] == nullptr) ? from : new LogicJoin (from, children[1]);
        }
        root = join;
    } else if (from_.size () == 1)
        root = transformOneFrom (from_[0]);

    return root;
}

/*
SELECT is implemented as if a query was executed in the following order:
1. CTEs: every one is evaluated and evaluated once as if it is served as temp table.
2. FROM clause: every one in from clause evaluated. They together evaluated as a Cartesian join.
3. WHERE clause: filters, including joins filters are evaluated.
4. GROUP BY clause: grouping according to group by clause and filtered by HAVING clause.
5. SELECT [ALL|DISTINCT] clause: ALL (default) will output every row and DISTINCT removes
duplicates.
6. Set Ops: UION [ALL] | INTERSECT| EXCEPT combines multiple output of SELECT.
7. ORDER BY clause: sort the results with the specified order.
8. LIMIT|FETCH|OFFSET clause: restrict amount of results output.
*/
LogicNode* SelectStmt::CreatePlan () {
    LogicNode* root = transformFromClause ();
    return root;
}
