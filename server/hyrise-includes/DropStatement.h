#ifndef __DROPSTATEMENT_H_
#define __DROPSTATEMENT_H_
namespace andb {
struct DropStatement : public SQLStatement {

    virtual LogicNode* CreatePlan (void) {
        return new LogicNode ();
    }

};
}
#endif // __DROPSTATEMENT_H_
