#ifndef __EXECUTESTATEMENT_H_
#define __EXECUTESTATEMENT_H_
namespace andb {
struct ExecuteStatement : public SQLStatement {
    ExecuteStatement ();
    ~ExecuteStatement () override;

    char* name;
    std::vector<Expr*>* parameters;

    virtual LogicNode* CreatePlan (void) {
        throw "illegal virtual call";
    }
};
}
#endif // __EXECUTESTATEMENT_H_
