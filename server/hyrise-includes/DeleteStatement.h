#ifndef __DELETESTATEMENT_H_
#define __DELETESTATEMENT_H_
namespace andb {
struct DeleteStatement : public SQLStatement {

    virtual LogicNode* CreatePlan (void) {
        return new LogicNode ();
    }

};
}
#endif // __DELETESTATEMENT_H_
